// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Edict.Engine;

namespace Edict.Net;

/// <summary>
/// net_udp.c (and net_wins.c, its Winsock twin): the datagram driver's LAN driver over UDP/IPv4. Sockets are
/// non-blocking and bound to any address; a read with nothing waiting (or a refused port's ICMP) gives 0, an address
/// with the host 0.0.0.0 or 127.0.0.1 is this machine's (myAddr), a name of digits a partial address filled in from
/// it (PartialIPAddress), another name looked up. The calls go to ws2_32 on Windows and to libc on Linux, without
/// managed allocations in the frame's reads and writes.
/// </summary>
public sealed unsafe partial class UdpLan : INetLan
{
    private const int MaxSockets = 64, AfInet = 2, SockDgram = 2, IpprotoUdp = 17;
    private const string FrameCall = "a socket call with blittable arguments on a non-blocking socket: nothing managed is allocated, nothing waits";

    private static readonly bool Windows = OperatingSystem.IsWindows();
    private static readonly SysErrorException FionReadFailed = new("UDP: ioctlsocket (FIONREAD) failed");

    private readonly nint[] _handles = new nint[MaxSockets];     // the sockets by the driver's numbers; -1 free
    private int _acceptSocket = -1, _controlSocket = -1, _broadcastSocket = -1;
    private uint _myAddr;                                        // network order, as sin_addr.s_addr
    private readonly byte[] _addrText = new byte[32];

    /// <summary>net_hostport: the port the accept socket listens on (-port, the "port" command).</summary>
    public int HostPort { get; set; } = Datagram.DefaultHostPort;

    /// <summary>The machine's name (gethostname), which becomes hostname when it is still "UNNAMED".</summary>
    public string MachineName { get; private set; } = "";

    /// <summary>my_tcpip_address: this machine's address, without the port.</summary>
    public string MyAddress { get; private set; } = "";

    public UdpLan()
    {
        for (int i = 0; i < MaxSockets; i++)
            _handles[i] = -1;
    }

    // ---- the system's calls

    private static class Win
    {
        public const int SolSocket = 0xffff, SoBroadcast = 0x20, WsaEWouldBlock = 10035, WsaEConnReset = 10054,
            WsaEConnRefused = 10061;
        public const uint FionBio = 0x8004667E, FionRead = 0x4004667F, SioUdpConnReset = 0x9800000C;
    }

    private static class Posix
    {
        public const int SolSocket = 1, SoBroadcast = 6, EAgain = 11, EConnRefused = 111;
        public const nuint FionBio = 0x5421, FionRead = 0x541B;
    }

    [LibraryImport("ws2_32.dll", EntryPoint = "WSAStartup")]
    private static partial int WsaStartup(ushort version, byte* data);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("ws2_32.dll", EntryPoint = "socket")]
    private static partial nint WinSocket(int af, int type, int protocol);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("ws2_32.dll", EntryPoint = "bind")]
    private static partial int WinBind(nint s, byte* name, int namelen);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("ws2_32.dll", EntryPoint = "ioctlsocket")]
    private static partial int WinIoctl(nint s, uint cmd, uint* argp);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("ws2_32.dll", EntryPoint = "recvfrom")]
    private static partial int WinRecvFrom(nint s, byte* buf, int len, int flags, byte* from, int* fromlen);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("ws2_32.dll", EntryPoint = "sendto")]
    private static partial int WinSendTo(nint s, byte* buf, int len, int flags, byte* to, int tolen);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("ws2_32.dll", EntryPoint = "closesocket")]
    private static partial int WinClose(nint s);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("ws2_32.dll", EntryPoint = "getsockname")]
    private static partial int WinGetSockName(nint s, byte* name, int* namelen);

    [LibraryImport("ws2_32.dll", EntryPoint = "setsockopt")]
    private static partial int WinSetSockOpt(nint s, int level, int optname, byte* optval, int optlen);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("ws2_32.dll", EntryPoint = "WSAGetLastError")]
    private static partial int WsaGetLastError();

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("ws2_32.dll", EntryPoint = "WSAIoctl")]
    private static partial int WsaIoctl(nint s, uint code, void* inBuf, uint inLen, void* outBuf, uint outLen, uint* returned,
                                        nint overlapped, nint routine);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static partial int PosixSocket(int domain, int type, int protocol);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("libc", EntryPoint = "bind", SetLastError = true)]
    private static partial int PosixBind(int fd, byte* addr, uint len);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int PosixIoctl(int fd, nuint request, int* argp);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("libc", EntryPoint = "recvfrom", SetLastError = true)]
    private static partial nint PosixRecvFrom(int fd, byte* buf, nuint len, int flags, byte* from, uint* fromlen);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("libc", EntryPoint = "sendto", SetLastError = true)]
    private static partial nint PosixSendTo(int fd, byte* buf, nuint len, int flags, byte* to, uint tolen);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int PosixClose(int fd);

    [NoAlloc(Constraints.All, Reason = FrameCall)]
    [LibraryImport("libc", EntryPoint = "getsockname", SetLastError = true)]
    private static partial int PosixGetSockName(int fd, byte* addr, uint* len);

    [LibraryImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static partial int PosixSetSockOpt(int fd, int level, int optname, byte* optval, uint optlen);

    [NoAlloc(Constraints.All, Reason = "the error of the last call, kept by the marshaller; no allocation")]
    private static int LastError() => Windows ? WsaGetLastError() : Marshal.GetLastPInvokeError();

    // ---- net_udp.c

    /// <summary>UDP_Init: this machine's name and address, the control socket; -1 when sockets cannot start (-noudp).</summary>
    public int Init()
    {
        if (Windows)
        {
            byte* data = stackalloc byte[512];
            if (WsaStartup(0x0101, data) != 0)
                return -1;
        }
        // determine my name & address
        MachineName = Dns.GetHostName();
        IPAddress? mine = null;
        try
        {
            mine = Dns.GetHostAddresses(MachineName).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
        }
        mine ??= IPAddress.Loopback;
        _myAddr = BitConverter.ToUInt32(mine.GetAddressBytes(), 0);
        _controlSocket = OpenSocket(0);
        if (_controlSocket == -1)
            return -1;
        GetSocketAddr(_controlSocket, out QSockAddr addr);
        AddrToString(ref addr, _addrText);
        string text = ZString(_addrText);
        int colon = text.LastIndexOf(':');
        MyAddress = colon >= 0 ? text[..colon] : text;
        return _controlSocket;
    }

    /// <summary>UDP_Shutdown.</summary>
    public void Shutdown()
    {
        Listen(false);
        CloseSocket(_controlSocket);
    }

    /// <summary>UDP_Listen: the accept socket on net_hostport opened or closed.</summary>
    public void Listen(bool state)
    {
        if (state)
        {
            if (_acceptSocket != -1)
                return;
            _acceptSocket = OpenSocket(HostPort);
            if (_acceptSocket == -1)
                throw new SysErrorException("UDP_Listen: Unable to open accept socket");
            return;
        }
        if (_acceptSocket == -1)
            return;
        CloseSocket(_acceptSocket);
        _acceptSocket = -1;
    }

    /// <summary>UDP_OpenSocket: a non-blocking UDP socket bound to any address and the port (0: one the system picks).</summary>
    public int OpenSocket(int port)
    {
        int slot = -1;
        for (int i = 0; i < MaxSockets; i++)
            if (_handles[i] == -1)
            {
                slot = i;
                break;
            }
        if (slot == -1)
            return -1;
        byte* address = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            address[i] = 0;
        address[0] = AfInet;                    // sin_family (little-endian on both: the low byte first)
        address[2] = (byte)(port >> 8);         // sin_port, network order; sin_addr INADDR_ANY
        address[3] = (byte)port;
        nint h;
        if (Windows)
        {
            h = WinSocket(AfInet, SockDgram, IpprotoUdp);
            if (h == -1)
                return -1;
            uint one = 1, returned = 0, off = 0;
            if (WinIoctl(h, Win.FionBio, &one) != 0 || WinBind(h, address, 16) != 0)
            {
                WinClose(h);
                return -1;
            }
            // a datagram to a closed port must not make the next read fail (WSAECONNRESET)
            WsaIoctl(h, Win.SioUdpConnReset, &off, 4, null, 0, &returned, 0, 0);
        }
        else
        {
            int fd = PosixSocket(AfInet, SockDgram, IpprotoUdp);
            if (fd == -1)
                return -1;
            int one = 1;
            if (PosixIoctl(fd, Posix.FionBio, &one) == -1 || PosixBind(fd, address, 16) == -1)
            {
                PosixClose(fd);
                return -1;
            }
            h = fd;
        }
        _handles[slot] = h;
        return slot;
    }

    /// <summary>UDP_CloseSocket.</summary>
    public int CloseSocket(int socket)
    {
        if (socket < 0 || _handles[socket] == -1)
            return -1;
        if (socket == _broadcastSocket)
            _broadcastSocket = -1;
        nint h = _handles[socket];
        _handles[socket] = -1;
        return Windows ? WinClose(h) : PosixClose((int)h);
    }

    /// <summary>UDP_Connect: nothing to do for a datagram socket.</summary>
    public int Connect(int socket, ref QSockAddr addr) => 0;

    /// <summary>UDP_CheckNewConnections: the accept socket when a datagram waits on it (FIONREAD).</summary>
    public int CheckNewConnections()
    {
        if (_acceptSocket == -1)
            return -1;
        nint h = _handles[_acceptSocket];
        int available;
        if (Windows)
        {
            uint n = 0;
            if (WinIoctl(h, Win.FionRead, &n) != 0)
                throw FionReadFailed;
            available = (int)n;
        }
        else
        {
            int n = 0;
            if (PosixIoctl((int)h, Posix.FionRead, &n) == -1)
                throw FionReadFailed;
            available = n;
        }
        return available != 0 ? _acceptSocket : -1;
    }

    /// <summary>UDP_Read: a datagram and its sender; 0 when none waits (or the last write found a closed port).</summary>
    public int Read(int socket, byte* buf, int len, out QSockAddr addr)
    {
        addr = default;
        nint h = _handles[socket];
        int ret;
        fixed (QSockAddr* a = &addr)
        {
            if (Windows)
            {
                int alen = 16;
                ret = WinRecvFrom(h, buf, len, 0, (byte*)a, &alen);
            }
            else
            {
                uint alen = 16;
                ret = (int)PosixRecvFrom((int)h, buf, (nuint)len, 0, (byte*)a, &alen);
            }
        }
        if (ret == -1)
        {
            int e = LastError();
            if (Windows ? e is Win.WsaEWouldBlock or Win.WsaEConnReset or Win.WsaEConnRefused : e is Posix.EAgain or Posix.EConnRefused)
                return 0;
        }
        return ret;
    }

    /// <summary>UDP_Write: 0 when the system would block.</summary>
    public int Write(int socket, byte* buf, int len, ref QSockAddr addr)
    {
        nint h = _handles[socket];
        int ret;
        fixed (QSockAddr* a = &addr)
            ret = Windows ? WinSendTo(h, buf, len, 0, (byte*)a, 16) : (int)PosixSendTo((int)h, buf, (nuint)len, 0, (byte*)a, 16);
        if (ret == -1 && LastError() == (Windows ? Win.WsaEWouldBlock : Posix.EAgain))
            return 0;
        return ret;
    }

    /// <summary>UDP_Broadcast: to 255.255.255.255 on net_hostport, the socket made broadcast-capable once.</summary>
    public int Broadcast(int socket, byte* buf, int len)
    {
        if (socket != _broadcastSocket)
        {
            if (_broadcastSocket != -1)
                throw new SysErrorException("Attempted to use multiple broadcasts sockets");
            int one = 1;
            nint h = _handles[socket];
            int r = Windows ? WinSetSockOpt(h, Win.SolSocket, Win.SoBroadcast, (byte*)&one, 4)
                            : PosixSetSockOpt((int)h, Posix.SolSocket, Posix.SoBroadcast, (byte*)&one, 4);
            if (r < 0)
                return -1;
            _broadcastSocket = socket;
        }
        QSockAddr to = default;
        byte* t = (byte*)&to;
        t[0] = AfInet;
        t[2] = (byte)(HostPort >> 8);
        t[3] = (byte)HostPort;
        t[4] = t[5] = t[6] = t[7] = 255;
        return Write(socket, buf, len, ref to);
    }

    [NoAlloc(Constraints.All, Reason = "arithmetic on the address's bytes")]
    private static int Port(QSockAddr* a) => (((byte*)a)[2] << 8) | ((byte*)a)[3];

    /// <summary>UDP_AddrToString: "a.b.c.d:port".</summary>
    public void AddrToString(ref QSockAddr addr, Span<byte> text)
    {
        fixed (QSockAddr* a = &addr)
        {
            byte* ip = (byte*)a + 4;
            int n = 0;
            for (int i = 0; i < 4; i++)
            {
                n += Edict.Exact.CFormat.Int(ip[i], text[n..]);
                text[n++] = i < 3 ? (byte)'.' : (byte)':';
            }
            n += Edict.Exact.CFormat.Int(Port(a), text[n..]);
            text[n] = 0;
        }
    }

    /// <summary>UDP_StringToAddr: "a.b.c.d:port".</summary>
    public int StringToAddr(byte* text, out QSockAddr addr)
    {
        string s = ZString(text);
        string[] hostPort = s.Split(':');
        string[] parts = hostPort[0].Split('.');
        addr = default;
        fixed (QSockAddr* a = &addr)
        {
            byte* b = (byte*)a;
            b[0] = AfInet;
            for (int i = 0; i < 4 && i < parts.Length; i++)
                b[4 + i] = (byte)(int.TryParse(parts[i], out int v) ? v : 0);
            int port = hostPort.Length > 1 && int.TryParse(hostPort[1], out int p) ? p : 0;
            b[2] = (byte)(port >> 8);
            b[3] = (byte)port;
        }
        return 0;
    }

    /// <summary>UDP_GetSocketAddr: the socket's address; any address or the loopback's is this machine's.</summary>
    public int GetSocketAddr(int socket, out QSockAddr addr)
    {
        addr = default;
        nint h = _handles[socket];
        fixed (QSockAddr* a = &addr)
        {
            if (Windows)
            {
                int len = 16;
                WinGetSockName(h, (byte*)a, &len);
            }
            else
            {
                uint len = 16;
                PosixGetSockName((int)h, (byte*)a, &len);
            }
            uint* ip = (uint*)((byte*)a + 4);
            if (*ip == 0 || *ip == 0x0100007f)      // 0.0.0.0 or 127.0.0.1 in network order
                *ip = _myAddr;
        }
        return 0;
    }

    /// <summary>UDP_GetNameFromAddr: the host's name, or the address as text.</summary>
    public int GetNameFromAddr(ref QSockAddr addr, Span<byte> name)
    {
        AddrToString(ref addr, name);
        try
        {
            byte[] ip = new byte[4];
            fixed (QSockAddr* a = &addr)
                new ReadOnlySpan<byte>((byte*)a + 4, 4).CopyTo(ip);
            string host = Dns.GetHostEntry(new IPAddress(ip)).HostName;
            byte[] b = Encoding.Latin1.GetBytes(host);
            int n = Math.Min(b.Length, QSocket.NetNameLen - 1);
            b.AsSpan(0, n).CopyTo(name);
            name[n] = 0;
        }
        catch (SocketException)
        {
        }
        return 0;
    }

    /// <summary>UDP_GetAddrFromName: a partial address (digits) filled in from this machine's, else a name looked up.</summary>
    public int GetAddrFromName(byte* name, out QSockAddr addr)
    {
        addr = default;
        if (name[0] >= '0' && name[0] <= '9')
            return PartialIpAddress(ZString(name), ref addr);
        IPAddress? ip;
        try
        {
            ip = Dns.GetHostAddresses(ZString(name)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            return -1;
        }
        if (ip is null)
            return -1;
        fixed (QSockAddr* a = &addr)
        {
            byte* b = (byte*)a;
            b[0] = AfInet;
            b[2] = (byte)(HostPort >> 8);
            b[3] = (byte)HostPort;
            ip.GetAddressBytes().CopyTo(new Span<byte>(b + 4, 4));
        }
        return 0;
    }

    /// <summary>PartialIPAddress: as much of the address as typed, the rest from this machine's; ":port", else net_hostport.</summary>
    private int PartialIpAddress(string text, ref QSockAddr hostaddr)
    {
        string buff = "." + text;
        int b = buff.Length > 1 && buff[1] == '.' ? 1 : 0;
        int addr = 0, mask = -1;
        while (b < buff.Length && buff[b] == '.')
        {
            b++;
            int num = 0, run = 0;
            while (b < buff.Length && buff[b] >= '0' && buff[b] <= '9')
            {
                num = num * 10 + buff[b++] - '0';
                if (++run > 3)
                    return -1;
            }
            char c = b < buff.Length ? buff[b] : '\0';
            if ((c < '0' || c > '9') && c != '.' && c != ':' && c != 0)
                return -1;
            if (num < 0 || num > 255)
                return -1;
            mask <<= 8;
            addr = (addr << 8) + num;
        }
        int port = b < buff.Length && buff[b] == ':' && int.TryParse(buff[(b + 1)..], out int p) ? p : HostPort;
        uint netMask = (uint)IPAddress.HostToNetworkOrder(mask), netAddr = (uint)IPAddress.HostToNetworkOrder(addr);
        fixed (QSockAddr* a = &hostaddr)
        {
            byte* q = (byte*)a;
            q[0] = AfInet;
            q[1] = 0;
            q[2] = (byte)((short)port >> 8);
            q[3] = (byte)port;
            *(uint*)(q + 4) = (_myAddr & netMask) | netAddr;
        }
        return 0;
    }

    /// <summary>UDP_AddrCompare: -1 another family or host, 1 another port, 0 the same.</summary>
    public int AddrCompare(ref QSockAddr a, ref QSockAddr b)
    {
        fixed (QSockAddr* pa = &a, pb = &b)
        {
            if (pa->Family != pb->Family)
                return -1;
            if (*(uint*)((byte*)pa + 4) != *(uint*)((byte*)pb + 4))
                return -1;
            if (Port(pa) != Port(pb))
                return 1;
        }
        return 0;
    }

    /// <summary>UDP_GetSocketPort.</summary>
    public int GetSocketPort(ref QSockAddr addr)
    {
        fixed (QSockAddr* a = &addr)
            return Port(a);
    }

    /// <summary>UDP_SetSocketPort.</summary>
    public int SetSocketPort(ref QSockAddr addr, int port)
    {
        fixed (QSockAddr* a = &addr)
        {
            ((byte*)a)[2] = (byte)(port >> 8);
            ((byte*)a)[3] = (byte)port;
        }
        return 0;
    }

    private static string ZString(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(n < 0 ? s : s[..n]);
    }

    private static string ZString(byte* s)
    {
        int n = 0;
        while (s[n] != 0)
            n++;
        return Encoding.Latin1.GetString(s, n);
    }
}
