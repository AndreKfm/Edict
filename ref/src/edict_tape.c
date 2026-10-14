/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: tape network driver. Replaces net_none.c in the build (it defines the driver table).

A tape is a file of client-to-server messages, each tagged with the host frame from which it may be read
(docs/PLAN.md E.4). The driver presents the server its client connections and hands them the tape's messages;
everything the server sends is accepted, goes to the messages lane of the state hashes (edict_hash.c) and is then
discarded. Input is open-loop: nothing on the tape depends on what the server does, so the same file drives the C
reference and the C# port.

File layout (little-endian):
  header  "ETP1"  u32 version (1 or 2)  u32 seed  u32 frames
  record  u32 frame  u8 type  u16 length  byte data[length]                  version 1: one client
  record  u32 frame  u8 type  u8 client  u16 length  byte data[length]       version 2: clients 0 to 15
sorted by frame, then file order.
Types: 0 connect (length 0), 1 reliable message, 2 unreliable message (the values NET_GetMessage returns); from
version 2 also 3 error (NET_GetMessage returns -1: the connection broke), 4 send failure (from then on the
connection's sends return -1), 5 busy (NET_CanSendMessage is false from then on, a datagram connection waiting for
an ack) and 6 ready (true again).
A record with frame f is delivered when the server reads that client's messages during a host frame with
host_framecount >= f; the states 4 to 6 take effect when the server reads past them. A client's records after a
connect belong to that connection, up to the client's next connect, which needs the connection closed first
(SV_DropClient's NET_Close). SV_ReadClientMessage reads reliable messages until it gets an unreliable one, so a
tape holds, per client and frame, any reliable messages followed by at most one unreliable message.
In the messages lane a message's kind is its type plus 16 times its client (version 1: the type alone).

Argument: -edicttape <file>. Without it the driver does not initialize and the server runs without clients.
*/
#include "quakedef.h"
#include "edict_hash.h"

#define TAPE_CONNECT	0
#define TAPE_ERROR		3
#define TAPE_SENDFAIL	4
#define TAPE_BUSY		5
#define TAPE_READY		6
#define TAPE_CLIENTS	16

typedef struct
{
	qsocket_t	*sock;
	int			pos;			// the client's next record, or past it
	qboolean	sendfail, busy;
} tapeclient_t;

static byte		*tape;			// whole file
static int		tape_size;
static int		tape_version;
static int		tape_head;		// a record's header bytes: 7 in version 1, 8 in version 2
static int		connect_pos;	// next record CheckNewConnections looks at
static tapeclient_t	tape_clients[TAPE_CLIENTS];

static unsigned int ReadU32 (int pos)
{
	return tape[pos] | (tape[pos + 1] << 8) | (tape[pos + 2] << 16) | ((unsigned int)tape[pos + 3] << 24);
}

// The record at pos: its frame, type, client and length; false past the end.
static qboolean RecordAt (int pos, unsigned int *frame, int *type, int *client, int *length)
{
	if (pos + tape_head > tape_size)
		return false;
	*frame = ReadU32 (pos);
	*type = tape[pos + 4];
	*client = tape_version == 1 ? 0 : tape[pos + 5];
	*length = tape[pos + tape_head - 2] | (tape[pos + tape_head - 1] << 8);
	if (pos + tape_head + *length > tape_size)
		Sys_Error ("Tape: truncated record at offset %i", pos);
	if (*client >= TAPE_CLIENTS)
		Sys_Error ("Tape: client %i at offset %i", *client, pos);
	return true;
}

static int Tape_Init (void)
{
	int		i;
	FILE	*f;

	i = COM_CheckParm ("-edicttape");
	if (!i || i >= com_argc - 1)
		return -1;
	f = fopen (com_argv[i + 1], "rb");
	if (!f)
		Sys_Error ("Tape: cannot open %s", com_argv[i + 1]);
	fseek (f, 0, SEEK_END);
	tape_size = ftell (f);
	fseek (f, 0, SEEK_SET);
	tape = malloc (tape_size);
	if (!tape || fread (tape, 1, tape_size, f) != (size_t)tape_size)
		Sys_Error ("Tape: cannot read %s", com_argv[i + 1]);
	fclose (f);
	if (tape_size < 16 || memcmp (tape, "ETP1", 4) || (ReadU32 (4) != 1 && ReadU32 (4) != 2))
		Sys_Error ("Tape: %s is not a version 1 or 2 tape", com_argv[i + 1]);
	tape_version = ReadU32 (4);
	tape_head = tape_version == 1 ? 7 : 8;
	connect_pos = 16;
	memset (tape_clients, 0, sizeof(tape_clients));
	return 0;
}

static void Tape_Shutdown (void)
{
	free (tape);
	tape = NULL;
}

static void Tape_Listen (qboolean state)
{
}

static void Tape_SearchForHosts (qboolean xmit)
{
}

static qsocket_t *Tape_Connect (char *host)
{
	return NULL;
}

// The next due connect record, in file order: its client gets a connection, and its records start after it.
static qsocket_t *Tape_CheckNewConnections (void)
{
	unsigned int	frame;
	int		type, client, length;
	tapeclient_t	*c;

	while (RecordAt (connect_pos, &frame, &type, &client, &length))
	{
		if (frame > (unsigned int)host_framecount)
			return NULL;
		connect_pos += tape_head + length;
		if (type != TAPE_CONNECT)
			continue;
		c = &tape_clients[client];
		if (c->sock)
			Sys_Error ("Tape: client %i connects again while connected (offset %i)", client, connect_pos);
		c->sock = NET_NewQSocket ();
		if (!c->sock)
			Sys_Error ("Tape: no qsocket available");
		Q_strcpy (c->sock->address, "tape");
		c->sock->canSend = true;
		c->sock->driverdata = c;
		c->pos = connect_pos;
		c->sendfail = c->busy = false;
		return c->sock;
	}
	return NULL;
}

static int Tape_GetMessage (qsocket_t *sock)
{
	unsigned int	frame;
	int		type, client, length;
	tapeclient_t	*c = sock->driverdata;

	while (RecordAt (c->pos, &frame, &type, &client, &length))
	{
		if (client != c - tape_clients)
		{
			c->pos += tape_head + length;	// another client's
			continue;
		}
		if (type == TAPE_CONNECT || frame > (unsigned int)host_framecount)
			return 0;		// the next connection's, or not yet
		c->pos += tape_head + length;
		switch (type)
		{
		case 1:
		case 2:
			SZ_Clear (&net_message);
			SZ_Write (&net_message, tape + c->pos - length, length);
			return type;
		case TAPE_ERROR:
			return -1;
		case TAPE_SENDFAIL:
			c->sendfail = true;
			break;
		case TAPE_BUSY:
			c->busy = true;
			break;
		case TAPE_READY:
			c->busy = false;
			break;
		default:
			Sys_Error ("Tape: bad record type %i at offset %i", type, c->pos - tape_head - length);
		}
	}
	return 0;
}

static int TapeSend (qsocket_t *sock, int type, sizebuf_t *data)
{
	tapeclient_t	*c = sock->driverdata;

	if (c->sendfail)
		return -1;
	EdictHash_Sent (type + 16 * (int)(c - tape_clients), data->data, data->cursize);
	return 1;
}

static int Tape_SendMessage (qsocket_t *sock, sizebuf_t *data)
{
	return TapeSend (sock, 1, data);
}

static int Tape_SendUnreliableMessage (qsocket_t *sock, sizebuf_t *data)
{
	return TapeSend (sock, 2, data);
}

static qboolean Tape_CanSendMessage (qsocket_t *sock)
{
	return !((tapeclient_t *)sock->driverdata)->busy;
}

static qboolean Tape_CanSendUnreliableMessage (qsocket_t *sock)
{
	return true;
}

static void Tape_Close (qsocket_t *sock)
{
	((tapeclient_t *)sock->driverdata)->sock = NULL;
}

#ifndef EDICT_NET		// the network build has net_bsd.c's tables
net_driver_t net_drivers[MAX_NET_DRIVERS] =
{
	{
	"Tape",
	false,
	Tape_Init,
	Tape_Listen,
	Tape_SearchForHosts,
	Tape_Connect,
	Tape_CheckNewConnections,
	Tape_GetMessage,
	Tape_SendMessage,
	Tape_SendUnreliableMessage,
	Tape_CanSendMessage,
	Tape_CanSendUnreliableMessage,
	Tape_Close,
	Tape_Shutdown
	}
};
int net_numdrivers = 1;
#endif

/*
A listen server (P5, docs/PLAN.md): without -dedicated the table's one driver is net_loop.c's loopback instead, at
index 0 as in Quake's own tables (NET_SendToAll sends at once on driver 0; on another it would wait in a loop on a
clock that does not advance within a frame). Its sends from the server's socket go to the messages lane, as the
tape's do.
*/
#include "net_loop.h"

extern qsocket_t	*loop_server;	// net_loop.c

static int Edict_LoopSendMessage (qsocket_t *sock, sizebuf_t *data)
{
	if (sock == loop_server)
		EdictHash_Sent (1, data->data, data->cursize);
	return Loop_SendMessage (sock, data);
}

static int Edict_LoopSendUnreliableMessage (qsocket_t *sock, sizebuf_t *data)
{
	if (sock == loop_server)
		EdictHash_Sent (2, data->data, data->cursize);
	return Loop_SendUnreliableMessage (sock, data);
}

void EdictTape_UseLoopback (void)
{
	static net_driver_t	loop =
	{
	"Loopback",
	false,
	Loop_Init,
	Loop_Listen,
	Loop_SearchForHosts,
	Loop_Connect,
	Loop_CheckNewConnections,
	Loop_GetMessage,
	Edict_LoopSendMessage,
	Edict_LoopSendUnreliableMessage,
	Loop_CanSendMessage,
	Loop_CanSendUnreliableMessage,
	Loop_Close,
	Loop_Shutdown
	};

	net_drivers[0] = loop;
}

#ifndef EDICT_NET
net_landriver_t	net_landrivers[MAX_NET_DRIVERS];
int net_numlandrivers = 0;
#endif
