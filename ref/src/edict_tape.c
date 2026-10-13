/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict reference build: tape network driver. Replaces net_none.c in the build (it defines the driver table).

A tape is a file of client-to-server messages, each tagged with the host frame from which it may be read
(docs/PLAN.md E.4). The driver presents one client connection to the server and hands it the tape's messages;
everything the server sends is accepted, goes to the messages lane of the state hashes (edict_hash.c) and is then
discarded. Input is open-loop: nothing on the tape depends on what the server does, so the same file drives the C
reference and the C# port.

File layout (little-endian):
  header  "ETP1"  u32 version (1)  u32 seed  u32 frames
  record  u32 frame  u8 type  u16 length  byte data[length]       sorted by frame, then file order
Types: 0 connect (length 0), 1 reliable message, 2 unreliable message (the values NET_GetMessage returns).
A record with frame f is delivered when the server reads client messages during a host frame with
host_framecount >= f. SV_ReadClientMessage reads reliable messages until it gets an unreliable one, so a tape
holds, per frame, any reliable messages followed by at most one unreliable message.

Argument: -edicttape <file>. Without it the driver does not initialize and the server runs without clients.
*/
#include "quakedef.h"
#include "edict_hash.h"

#define TAPE_CONNECT	0

static byte		*tape;			// whole file
static int		tape_size;
static int		tape_pos;		// next record
static qsocket_t	*tape_sock;

static unsigned int ReadU32 (int pos)
{
	return tape[pos] | (tape[pos + 1] << 8) | (tape[pos + 2] << 16) | ((unsigned int)tape[pos + 3] << 24);
}

// Next record, if it is due in this host frame; returns false otherwise.
static qboolean NextRecord (int *type, int *length, byte **data)
{
	unsigned int	frame;

	if (tape_pos + 7 > tape_size)
		return false;
	frame = ReadU32 (tape_pos);
	if (frame > (unsigned int)host_framecount)
		return false;
	*type = tape[tape_pos + 4];
	*length = tape[tape_pos + 5] | (tape[tape_pos + 6] << 8);
	if (tape_pos + 7 + *length > tape_size)
		Sys_Error ("Tape: truncated record at offset %i", tape_pos);
	*data = tape + tape_pos + 7;
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
	if (tape_size < 16 || memcmp (tape, "ETP1", 4) || ReadU32 (4) != 1)
		Sys_Error ("Tape: %s is not a version 1 tape", com_argv[i + 1]);
	tape_pos = 16;
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

static qsocket_t *Tape_CheckNewConnections (void)
{
	int		type, length;
	byte	*data;

	if (!NextRecord (&type, &length, &data) || type != TAPE_CONNECT)
		return NULL;
	if (tape_sock)
		Sys_Error ("Tape: second connect record at offset %i", tape_pos);
	tape_pos += 7 + length;
	tape_sock = NET_NewQSocket ();
	if (!tape_sock)
		Sys_Error ("Tape: no qsocket available");
	Q_strcpy (tape_sock->address, "tape");
	tape_sock->canSend = true;
	return tape_sock;
}

static int Tape_GetMessage (qsocket_t *sock)
{
	int		type, length;
	byte	*data;

	if (!NextRecord (&type, &length, &data) || type == TAPE_CONNECT)
		return 0;
	if (type != 1 && type != 2)
		Sys_Error ("Tape: bad record type %i at offset %i", type, tape_pos);
	tape_pos += 7 + length;
	SZ_Clear (&net_message);
	SZ_Write (&net_message, data, length);
	return type;
}

static int Tape_SendMessage (qsocket_t *sock, sizebuf_t *data)
{
	EdictHash_Sent (1, data->data, data->cursize);
	return 1;
}

static int Tape_SendUnreliableMessage (qsocket_t *sock, sizebuf_t *data)
{
	EdictHash_Sent (2, data->data, data->cursize);
	return 1;
}

static qboolean Tape_CanSendMessage (qsocket_t *sock)
{
	return true;
}

static qboolean Tape_CanSendUnreliableMessage (qsocket_t *sock)
{
	return true;
}

static void Tape_Close (qsocket_t *sock)
{
	if (sock == tape_sock)
		tape_sock = NULL;
}

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

net_landriver_t	net_landrivers[MAX_NET_DRIVERS];
int net_numlandrivers = 0;
