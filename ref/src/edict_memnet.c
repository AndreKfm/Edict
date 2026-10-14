/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

Edict oracle: a LAN driver in memory for the level-1 tests of net_dgrm.c (docs/PLAN.md P6 step 9, docs/TESTING.md).
It behaves as net_udp.c towards net_dgrm.c (addresses AF_INET a.b.c.d:port, AddrCompare -1 for another family or
host, 1 for another port; Connect does nothing; CheckNewConnections answers the accept socket when a packet waits
for it; Read takes the oldest packet, 0 for none), but its packets go to a queue the test takes them from (what was
written, in order) and gives them to (what a socket reads, in the order and number the test chooses: loss,
duplicates, reordering, forged senders). Every Read advances the virtual clock by a step the test sets, so that
_Datagram_Connect's waits end. The port's twin (tests: MemNet) must answer the same calls the same way.
*/
#include "quakedef.h"
#include "net_dgrm.h"

#ifdef _WIN32
#define EDICT_EXPORT	__declspec(dllexport)
#else
#define EDICT_EXPORT	__attribute__((visibility("default")))
#endif

#ifndef AF_INET
#define AF_INET			2		// net_udp.c's family, as net_dgrm.c's ban test reads it
#endif
#define MEM_SOCKETS		32
#define MEM_QUEUE		64
#define MEM_PACKET		(NET_DATAGRAMSIZE + 64)

typedef struct
{
	int		len;
	unsigned char	data[MEM_PACKET];
	struct qsockaddr	addr;		// the sender's (in an inbox) or the receiver's (in the outbox)
	int		from;				// the writing socket (outbox)
} mempacket_t;

typedef struct
{
	qboolean	open;
	int			port;
	mempacket_t	inbox[MEM_QUEUE];
	int			head, count;
} memsock_t;

static memsock_t	mem_socks[MEM_SOCKETS];
static mempacket_t	mem_out[MEM_QUEUE];
static mempacket_t	mem_pending[MEM_QUEUE];	// packets for ports no socket has yet (from = the port)
static int			mem_pending_count;
static int			mem_out_count;
static unsigned int	mem_host;			// this host's IP address, network order bytes a.b.c.d
static int			mem_next_port;
static int			mem_accept = -1;
static double		mem_step;			// seconds of virtual clock per Read
extern double		edict_clock;		// edict_sys.c (Sys_FloatTime); the oracle has its own (edict_oracle.c)

static void SetAddr (struct qsockaddr *a, unsigned int ip, int port)
{
	memset (a, 0, sizeof(*a));
	a->sa_family = AF_INET;
	a->sa_data[0] = (unsigned char)(port >> 8);
	a->sa_data[1] = (unsigned char)port;
	memcpy (a->sa_data + 2, &ip, 4);
}

static int AddrPort (struct qsockaddr *a) { return (a->sa_data[0] << 8) | a->sa_data[1]; }

static int Mem_OpenSocket (int port)
{
	int		i;

	for (i = 0 ; i < MEM_SOCKETS ; i++)
		if (!mem_socks[i].open)
		{
			int		j, k;

			mem_socks[i].open = true;
			mem_socks[i].port = port ? port : mem_next_port++;
			mem_socks[i].head = mem_socks[i].count = 0;
			// the packets that waited for this port, in order
			for (j = 0, k = 0 ; j < mem_pending_count ; j++)
			{
				if (mem_pending[j].from == mem_socks[i].port && mem_socks[i].count < MEM_QUEUE)
					mem_socks[i].inbox[mem_socks[i].count++] = mem_pending[j];
				else
					mem_pending[k++] = mem_pending[j];
			}
			mem_pending_count = k;
			return i;
		}
	return -1;
}

static int Mem_CloseSocket (int socket)
{
	if (socket == mem_accept)
		mem_accept = -1;
	mem_socks[socket].open = false;
	return 0;
}

static int Mem_Init (void)
{
	return Mem_OpenSocket (0);		// the control socket
}

static void Mem_Shutdown (void)
{
}

static void Mem_Listen (qboolean state)
{
	if (state)
	{
		if (mem_accept == -1)
			mem_accept = Mem_OpenSocket (DEFAULTnet_hostport);
	}
	else if (mem_accept != -1)
		Mem_CloseSocket (mem_accept);
}

static int Mem_Connect (int socket, struct qsockaddr *addr)
{
	return 0;
}

static int Mem_CheckNewConnections (void)
{
	if (mem_accept == -1)
		return -1;
	return mem_socks[mem_accept].count ? mem_accept : -1;
}

static int Mem_Read (int socket, byte *buf, int len, struct qsockaddr *addr)
{
	memsock_t	*s = &mem_socks[socket];
	mempacket_t	*p;
	int			n;

	edict_clock += mem_step;
	if (!s->count)
		return 0;
	p = &s->inbox[s->head];
	s->head = (s->head + 1) % MEM_QUEUE;
	s->count--;
	n = p->len < len ? p->len : len;	// recvfrom cuts a datagram to the buffer
	memcpy (buf, p->data, n);
	*addr = p->addr;
	return n;
}

static int Mem_Write (int socket, byte *buf, int len, struct qsockaddr *addr)
{
	mempacket_t	*p;

	if (mem_out_count == MEM_QUEUE)
		Sys_Error ("MemNet: the outbox is full");
	if (len > MEM_PACKET)
		Sys_Error ("MemNet: a datagram of %i bytes", len);
	p = &mem_out[mem_out_count++];
	p->len = len;
	memcpy (p->data, buf, len);
	p->addr = *addr;
	p->from = socket;
	return len;
}

static int Mem_Broadcast (int socket, byte *buf, int len)
{
	struct qsockaddr	a;

	SetAddr (&a, 0xffffffff, DEFAULTnet_hostport);
	return Mem_Write (socket, buf, len, &a);
}

static char *Mem_AddrToString (struct qsockaddr *addr)
{
	static char buffer[22];
	unsigned char	*ip = addr->sa_data + 2;

	sprintf (buffer, "%d.%d.%d.%d:%d", ip[0], ip[1], ip[2], ip[3], AddrPort (addr));
	return buffer;
}

static int Mem_StringToAddr (char *string, struct qsockaddr *addr)
{
	int		ha1, ha2, ha3, ha4, hp;

	sscanf (string, "%d.%d.%d.%d:%d", &ha1, &ha2, &ha3, &ha4, &hp);
	SetAddr (addr, (unsigned int)(ha1 | (ha2 << 8) | (ha3 << 16) | ((unsigned int)ha4 << 24)), hp);
	return 0;
}

static int Mem_GetSocketAddr (int socket, struct qsockaddr *addr)
{
	SetAddr (addr, mem_host, mem_socks[socket].port);
	return 0;
}

static int Mem_GetNameFromAddr (struct qsockaddr *addr, char *name)
{
	Q_strcpy (name, Mem_AddrToString (addr));
	return 0;
}

// net_udp.c's PartialIPAddress and its default port: "a.b.c.d[:port]"
static int Mem_GetAddrFromName (char *name, struct qsockaddr *addr)
{
	int		ha1 = 0, ha2 = 0, ha3 = 0, ha4 = 0, port = DEFAULTnet_hostport;
	char	*colon;

	if (name[0] < '0' || name[0] > '9')
		return -1;		// no host names in memory
	if (sscanf (name, "%d.%d.%d.%d", &ha1, &ha2, &ha3, &ha4) != 4)
		return -1;
	colon = strchr (name, ':');
	if (colon)
		port = atoi (colon + 1);
	SetAddr (addr, (unsigned int)(ha1 | (ha2 << 8) | (ha3 << 16) | ((unsigned int)ha4 << 24)), port);
	return 0;
}

static int Mem_AddrCompare (struct qsockaddr *addr1, struct qsockaddr *addr2)
{
	if (addr1->sa_family != addr2->sa_family)
		return -1;
	if (memcmp (addr1->sa_data + 2, addr2->sa_data + 2, 4))
		return -1;
	if (AddrPort (addr1) != AddrPort (addr2))
		return 1;
	return 0;
}

static int Mem_GetSocketPort (struct qsockaddr *addr)
{
	return AddrPort (addr);
}

static int Mem_SetSocketPort (struct qsockaddr *addr, int port)
{
	addr->sa_data[0] = (unsigned char)(port >> 8);
	addr->sa_data[1] = (unsigned char)port;
	return 0;
}

static net_landriver_t	mem_driver =
{
	"MemNet",
	false,
	0,
	Mem_Init,
	Mem_Shutdown,
	Mem_Listen,
	Mem_OpenSocket,
	Mem_CloseSocket,
	Mem_Connect,
	Mem_CheckNewConnections,
	Mem_Read,
	Mem_Write,
	Mem_Broadcast,
	Mem_AddrToString,
	Mem_StringToAddr,
	Mem_GetSocketAddr,
	Mem_GetNameFromAddr,
	Mem_GetAddrFromName,
	Mem_AddrCompare,
	Mem_GetSocketPort,
	Mem_SetSocketPort
};

// ---- the oracle's entry points

// net_drivers[1] as Quake's own tables have it (net_win.c, net_bsd.c): the datagram driver after the loopback
static net_driver_t	dgrm_driver =
{
	"Datagram",
	true,
	Datagram_Init,
	Datagram_Listen,
	Datagram_SearchForHosts,
	Datagram_Connect,
	Datagram_CheckNewConnections,
	Datagram_GetMessage,
	Datagram_SendMessage,
	Datagram_SendUnreliableMessage,
	Datagram_CanSendMessage,
	Datagram_CanSendUnreliableMessage,
	Datagram_Close,
	Datagram_Shutdown
};

extern int packetsSent, packetsReSent, packetsReceived, receivedDuplicateCount, shortPacketCount, droppedDatagrams;
extern byte packetBuffer[NET_DATAGRAMSIZE];		// net_dgrm.c's packet buffer (a struct there; its bytes here)
short ShortSwap (short l), ShortNoSwap (short l);	// common.c
int LongSwap (int l), LongNoSwap (int l);
float FloatSwap (float f), FloatNoSwap (float f);

#define MEM_QSOCKETS	8
static qsocket_t	mem_qsockets[MEM_QSOCKETS];
static byte			mem_message[NET_MAXMESSAGE];	// net_message's buffer (NET_Init's SZ_Alloc)

/*
A fresh network: this host's address, the virtual clock and its step, maxclients (NET_NewQSocket's limit), the
qsocket pool (net_main.c's NET_Init), the driver in net_landrivers[0] and Datagram_Init (which opens the control
socket), listening (the accept socket on 26000) when asked.
*/
EDICT_EXPORT void EdictOracle_NetReset (unsigned int host, double clock, double step, int maxclients, int listen)
{
	int		i;

	// COM_Init's byte order functions (net_dgrm.c's BigLong), little-endian as the reference's hosts are
	BigShort = ShortSwap; LittleShort = ShortNoSwap;
	BigLong = LongSwap; LittleLong = LongNoSwap;
	BigFloat = FloatSwap; LittleFloat = FloatNoSwap;
	memset (packetBuffer, 0, NET_DATAGRAMSIZE);	// a fresh process's: a header claiming more than came reads it
	packetsSent = packetsReSent = packetsReceived = receivedDuplicateCount = shortPacketCount = droppedDatagrams = 0;
	memset (mem_socks, 0, sizeof(mem_socks));
	mem_out_count = 0;
	mem_pending_count = 0;
	net_message.data = mem_message;
	net_message.maxsize = sizeof(mem_message);
	net_message.cursize = 0;
	net_message.allowoverflow = false;
	net_message.overflowed = false;
	mem_host = host;
	mem_next_port = 27001;
	mem_accept = -1;
	mem_step = step;
	edict_clock = clock;
	net_time = clock;
	svs.maxclients = maxclients;
	net_activeconnections = 0;
	net_activeSockets = NULL;
	net_freeSockets = NULL;
	for (i = 0 ; i < MEM_QSOCKETS ; i++)
	{
		memset (&mem_qsockets[i], 0, sizeof(qsocket_t));
		mem_qsockets[i].next = net_freeSockets;
		net_freeSockets = &mem_qsockets[i];
		mem_qsockets[i].disconnected = true;
	}
	net_numsockets = MEM_QSOCKETS;
	net_landrivers[0] = mem_driver;
	net_numlandrivers = 1;
	net_drivers[1] = dgrm_driver;	// NET_Close calls a socket's driver through net_drivers
	net_driverlevel = 1;		// the datagram driver's place in net_drivers
	// Datagram_Init's part on the LAN drivers (its commands would need the hunk, which the oracle has not)
	for (i = 0 ; i < net_numlandrivers ; i++)
	{
		int		csock = net_landrivers[i].Init ();

		if (csock == -1)
			continue;
		net_landrivers[i].initialized = true;
		net_landrivers[i].controlSock = csock;
	}
	if (listen)
		Datagram_Listen (true);
}

EDICT_EXPORT void EdictOracle_NetSetTime (double t) { edict_clock = t; net_time = t; }
EDICT_EXPORT double EdictOracle_NetTime (void) { return net_time; }
EDICT_EXPORT double EdictOracle_NetClock (void) { return edict_clock; }

// the outbox: how many packets wait, and the oldest taken (its bytes, the writing socket's port, the address)
EDICT_EXPORT int EdictOracle_NetOutCount (void) { return mem_out_count; }

EDICT_EXPORT int EdictOracle_NetTakeOut (unsigned char *buf, int size, int *fromPort, unsigned char *addr16)
{
	mempacket_t	p;
	int			n;

	if (!mem_out_count)
		return -1;
	p = mem_out[0];
	memmove (mem_out, mem_out + 1, (mem_out_count - 1) * sizeof(mempacket_t));
	mem_out_count--;
	n = p.len < size ? p.len : size;
	memcpy (buf, p.data, n);
	*fromPort = mem_socks[p.from].port;
	memcpy (addr16, &p.addr, 16);
	return p.len;
}

// a packet into the inbox of the socket with this port, from the sender's 16-byte address; false: no such socket
EDICT_EXPORT int EdictOracle_NetGive (int port, const unsigned char *data, int len, const unsigned char *from16)
{
	int		i;
	memsock_t	*s;

	for (i = 0 ; i < MEM_SOCKETS ; i++)
		if (mem_socks[i].open && mem_socks[i].port == port)
			break;
	if (i == MEM_SOCKETS)
	{	// no socket has the port yet: it gets the packet when it opens
		if (mem_pending_count == MEM_QUEUE || len > MEM_PACKET)
			return 0;
		mem_pending[mem_pending_count].len = len;
		memcpy (mem_pending[mem_pending_count].data, data, len);
		memcpy (&mem_pending[mem_pending_count].addr, from16, 16);
		mem_pending[mem_pending_count].from = port;
		mem_pending_count++;
		return 2;
	}
	s = &mem_socks[i];
	if (s->count == MEM_QUEUE || len > MEM_PACKET)
		return 0;
	s->inbox[(s->head + s->count) % MEM_QUEUE].len = len;
	memcpy (s->inbox[(s->head + s->count) % MEM_QUEUE].data, data, len);
	memcpy (&s->inbox[(s->head + s->count) % MEM_QUEUE].addr, from16, 16);
	s->count++;
	return 1;
}

// net_dgrm.c's statistic counters (net_stats prints them)

EDICT_EXPORT void EdictOracle_NetCounters (int *c)
{
	c[0] = packetsSent; c[1] = packetsReSent; c[2] = packetsReceived;
	c[3] = receivedDuplicateCount; c[4] = shortPacketCount; c[5] = droppedDatagrams;
}

// a qsocket by its place in the pool, -1 for none
static qsocket_t *QSock (int i) { return i >= 0 && i < MEM_QSOCKETS ? &mem_qsockets[i] : NULL; }
static int QIndex (qsocket_t *s) { return s ? (int)(s - mem_qsockets) : -1; }

EDICT_EXPORT int EdictOracle_NetCheckNewConnections (void) { return QIndex (Datagram_CheckNewConnections ()); }
EDICT_EXPORT int EdictOracle_NetConnect (char *host) { return QIndex (Datagram_Connect (host)); }
EDICT_EXPORT int EdictOracle_NetGetMessage (int s) { return Datagram_GetMessage (QSock (s)); }
EDICT_EXPORT int EdictOracle_NetCanSendMessage (int s) { return Datagram_CanSendMessage (QSock (s)); }
EDICT_EXPORT void EdictOracle_NetClose (int s) { NET_Close (QSock (s)); }

static sizebuf_t	mem_data;
static byte			mem_data_buf[NET_MAXMESSAGE];

EDICT_EXPORT int EdictOracle_NetSendMessage (int s, const unsigned char *data, int len)
{
	mem_data.data = mem_data_buf;
	mem_data.maxsize = sizeof(mem_data_buf);
	mem_data.cursize = len;
	memcpy (mem_data_buf, data, len);
	return Datagram_SendMessage (QSock (s), &mem_data);
}

EDICT_EXPORT int EdictOracle_NetSendUnreliableMessage (int s, const unsigned char *data, int len)
{
	mem_data.data = mem_data_buf;
	mem_data.maxsize = sizeof(mem_data_buf);
	mem_data.cursize = len;
	memcpy (mem_data_buf, data, len);
	return Datagram_SendUnreliableMessage (QSock (s), &mem_data);
}

// net_message as the last call left it
EDICT_EXPORT int EdictOracle_NetMessage (unsigned char *buf, int size)
{
	int		n = net_message.cursize < size ? net_message.cursize : size;

	memcpy (buf, net_message.data, n);
	return net_message.cursize;
}

// a qsocket's state: the flags, sequences and lengths, its times, its address and address text
EDICT_EXPORT void EdictOracle_NetSocketState (int s, int *ints, double *times, unsigned char *addr16, char *address)
{
	qsocket_t	*q = QSock (s);

	ints[0] = q->disconnected; ints[1] = q->canSend; ints[2] = q->sendNext; ints[3] = q->socket;
	ints[4] = q->ackSequence; ints[5] = q->sendSequence; ints[6] = q->unreliableSendSequence;
	ints[7] = q->sendMessageLength; ints[8] = q->receiveSequence; ints[9] = q->unreliableReceiveSequence;
	ints[10] = q->receiveMessageLength; ints[11] = net_activeconnections;
	times[0] = q->connecttime; times[1] = q->lastMessageTime; times[2] = q->lastSendTime;
	memcpy (addr16, &q->addr, 16);
	Q_strcpy (address, q->address);
}

// the server's state CCREQ_SERVER_INFO reports: hostname, sv.name, net_activeconnections
static char	mem_hostname[64];

EDICT_EXPORT void EdictOracle_NetServerInfo (char *name, char *map, int active)
{
	Q_strncpy (mem_hostname, name, sizeof(mem_hostname) - 1);
	hostname.string = mem_hostname;		// the cvar's text (the oracle registers no cvars)
	Q_strcpy (sv.name, map);
	net_activeconnections = active;
}
