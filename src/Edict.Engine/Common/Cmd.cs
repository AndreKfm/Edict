// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace Edict.Engine;

/// <summary>
/// cmd.c: the command buffer (Cbuf_*), the tokenizer, the command table, aliases and Cvar_Command, and the commands
/// cmd.c registers itself (stuffcmds, exec, echo, alias, wait). The table's names are the reference's
/// (<see cref="Registry.Commands"/>); a host registers the commands it ports, and a registered command it does not
/// port stops with an error rather than being taken for an unknown one.
/// </summary>
[NoAllocUnchecked("console commands: tokenizing and running a command allocates, as in C (Z_Malloc); the frames that run one are counted apart")]
public sealed unsafe class Cmd
{
    public const int MaxArgs = 80, MaxAliasName = 32, LineSize = 1024;

    // cmd_source_t
    public const int SrcClient = 0, SrcCommand = 1;

    private readonly Cvars _cvars;
    private readonly QFileSystem _files;
    private readonly ComParse _parse = new();
    private readonly Dictionary<string, Action> _commands = new(StringComparer.Ordinal);
    private readonly HashSet<string> _registered;
    private readonly List<(byte[] Name, byte[] Value)> _aliases = [];     // cmd_alias: the newest first
    private readonly List<byte[]> _argv = [];
    private byte[] _args = [];
    private bool _wait;

    /// <summary>cmd_text.</summary>
    public SizeBuf Text { get; } = new(8192);

    /// <summary>The console Con_Printf writes to; null for a dedicated server (ca_dedicated: no output).</summary>
    public IConsole? Con { get; set; }

    /// <summary>MAX_NUM_ARGVS: COM_InitArgv keeps this many arguments, the program's name among them.</summary>
    public const int MaxNumArgvs = 50;

    /// <summary>com_argv after the program name, for stuffcmds: at most <see cref="MaxNumArgvs"/> - 1 of them.</summary>
    public IReadOnlyList<string> CommandLine
    {
        get => _commandLine;
        set => _commandLine = value.Count < MaxNumArgvs ? value : value.Take(MaxNumArgvs - 1).ToArray();
    }

    private IReadOnlyList<string> _commandLine = [];

    /// <summary>cmd_source.</summary>
    public int Source { get; private set; }

    /// <summary>Commands run so far (the allocation gate counts their frames apart).</summary>
    public int Executed { get; private set; }

    public Cmd(Cvars cvars, QFileSystem files)
    {
        _cvars = cvars;
        _files = files;
        _registered = new HashSet<string>(Registry.Commands.Select(c => c.ToLowerInvariant()), StringComparer.Ordinal);
        Add("stuffcmds", StuffCmds);
        Add("exec", Exec);
        Add("echo", Echo);
        Add("alias", Alias);
        Add("wait", () => _wait = true);
    }

    /// <summary>A player's game (a platform): a command of the table the port lacks prints, instead of stopping the run.</summary>
    public bool Interactive { get; set; }

    /// <summary>
    /// Cmd_CompleteCommand, then Cvar_CompleteVariable: the first command (newest first, as cmd_functions is linked)
    /// and then cvar whose name begins with the partial text; null for none or an empty text.
    /// </summary>
    public byte[]? Complete(ReadOnlySpan<byte> partial)
    {
        if (partial.Length == 0)
            return null;
        string p = System.Text.Encoding.Latin1.GetString(partial);
        for (int i = Registry.Commands.Length - 1; i >= 0; i--)
            if (Registry.Commands[i].StartsWith(p, StringComparison.Ordinal))
                return System.Text.Encoding.Latin1.GetBytes(Registry.Commands[i]);
        if (_sound)
            for (int i = Sound.Commands.Length - 1; i >= 0; i--)
                if (Sound.Commands[i].StartsWith(p, StringComparison.Ordinal))
                    return System.Text.Encoding.Latin1.GetBytes(Sound.Commands[i]);
        foreach (Registry.CvarDef c in _sound ? Sound.RegistryCvars() : Registry.Cvars)
            if (c.Name.StartsWith(p, StringComparison.Ordinal))
                return System.Text.Encoding.Latin1.GetBytes(c.Name);
        return null;
    }

    private bool _sound;

    /// <summary>
    /// The sound build's table (S_Init's commands, <see cref="Sound.Commands"/>) for a host with sound: before
    /// S_Init registers them. Completion finds them after the table's (C's newest first is approximated: S_Init's
    /// come after most of Host_Init's), and S_Init's cvars in the cvar list.
    /// </summary>
    public void UseSoundTable()
    {
        _sound = true;
        foreach (string c in Sound.Commands)
            _registered.Add(c);
    }

    /// <summary>Cmd_AddCommand for a command of the reference's table.</summary>
    public void Add(string name, Action handler)
    {
        string key = name.ToLowerInvariant();
        if (!_registered.Contains(key))
            throw new InvalidOperationException($"Cmd_AddCommand: \"{name}\" is not a command of the reference");
        _commands[key] = handler;
    }

    /// <summary>Cmd_Argc.</summary>
    public int Argc => _argv.Count;

    /// <summary>Cmd_Argv: the argument, "" past the end.</summary>
    public byte[] Argv(int i) => i < _argv.Count ? _argv[i] : [];

    /// <summary>Cmd_Args: the text after the command's name (cmd_args), "" for none.</summary>
    public byte[] Args => _args;

    // ---- the command buffer

    /// <summary>Cbuf_AddText: adds command text at the end of the buffer.</summary>
    public void AddText(ReadOnlySpan<byte> text)
    {
        if (Text.CurSize + text.Length >= Text.MaxSize)
        {
            Printf("Cbuf_AddText: overflow\n"u8);
            return;
        }
        fixed (byte* t = text)
            Text.Write(t, text.Length);
    }

    private static readonly SysErrorException NoAllowOverflow = new("SZ_GetSpace: overflow without allowoverflow set");

    /// <summary>Cbuf_InsertText: adds command text immediately after the current command.</summary>
    public void InsertText(ReadOnlySpan<byte> text)
    {
        byte[] rest = new ReadOnlySpan<byte>(Text.Data, Text.CurSize).ToArray();
        Text.Clear();
        AddText(text);
        if (rest.Length == 0)
            return;
        if (Text.CurSize + rest.Length > Text.MaxSize)
            throw NoAllowOverflow;
        fixed (byte* r = rest)
            Text.Write(r, rest.Length);
    }

    /// <summary>Cbuf_Execute: every line (to a newline, or a ; outside quotes) of the buffer, until a wait.</summary>
    public void Execute()
    {
        byte* line = stackalloc byte[LineSize];
        while (Text.CurSize != 0)
        {
            // find a \n or ; line break
            byte* text = Text.Data;
            int quotes = 0, i;
            for (i = 0; i < Text.CurSize; i++)
            {
                if (text[i] == '"')
                    quotes++;
                if ((quotes & 1) == 0 && text[i] == ';')
                    break;  // don't break if inside a quoted string
                if (text[i] == '\n')
                    break;
            }
            if (i >= LineSize)
                throw new InvalidOperationException("Cbuf_Execute: a line longer than C's line[1024]");
            for (int k = 0; k < i; k++)
                line[k] = text[k];
            line[i] = 0;

            // delete the text from the command buffer and move remaining commands down; this is necessary because
            // commands (exec, alias) can insert data at the beginning of the text buffer
            if (i == Text.CurSize)
                Text.CurSize = 0;
            else
            {
                i++;
                Text.CurSize -= i;
                for (int k = 0; k < Text.CurSize; k++)
                    text[k] = text[k + i];
            }

            // execute the command line
            ExecuteString(line, SrcCommand);

            if (_wait)
            {
                // skip out while text still remains in buffer, leaving it for next frame
                _wait = false;
                break;
            }
        }
    }

    // ---- execution

    /// <summary>Cmd_TokenizeString: parses the given string into command line tokens.</summary>
    private void Tokenize(byte* text)
    {
        _argv.Clear();
        _args = [];
        while (true)
        {
            // skip whitespace up to a /n
            while (*text != 0 && (sbyte)*text <= ' ' && *text != '\n')
                text++;
            if (*text == '\n')
                break;      // a newline seperates commands in the buffer
            if (*text == 0)
                return;
            if (_argv.Count == 1)
            {
                int n = 0;
                while (text[n] != 0)
                    n++;
                _args = new ReadOnlySpan<byte>(text, n).ToArray();
            }
            text = _parse.Parse(text);
            if (text == null)
                return;
            if (_argv.Count < MaxArgs)
            {
                int n = 0;
                while (_parse.Token[n] != 0)
                    n++;
                _argv.Add(new ReadOnlySpan<byte>(_parse.Token, n).ToArray());
            }
        }
    }

    /// <summary>
    /// Cmd_ExecuteString: parses a single line of text into arguments and tries to execute it as a command, an
    /// alias or a cvar (case-insensitive for the first two, as Q_strcasecmp).
    /// </summary>
    public void ExecuteString(byte* text, int src)
    {
        Source = src;
        Tokenize(text);
        // execute the command line
        if (_argv.Count == 0)
            return;     // no tokens
        Executed++;

        // check functions
        string name = Encoding.Latin1.GetString(_argv[0]).ToLowerInvariant();
        if (_registered.Contains(name))
        {
            if (!_commands.TryGetValue(name, out Action? handler))
            {
                if (Interactive)
                {
                    Msg("\""u8).Msg(_argv[0]).Msg("\" is not ported yet\n"u8).End();
                    return;
                }
                throw new InvalidOperationException($"the console command \"{name}\" is not ported");
            }
            handler();
            return;
        }
        // check alias
        foreach ((byte[] aliasName, byte[] value) in _aliases)
            if (CaseEquals(aliasName, _argv[0]))
            {
                InsertText(value);
                return;
            }
        // check cvars
        if (!CvarCommand())
            Msg("Unknown command \""u8).Msg(_argv[0]).Msg("\"\n"u8).End();
    }

    /// <summary>Cmd_ExecuteString of a C# string (tests and hosts).</summary>
    public void ExecuteString(string text, int src = SrcCommand)
    {
        byte[] b = Encoding.Latin1.GetBytes(text + "\0");
        fixed (byte* t = b)
            ExecuteString(t, src);
    }

    private static bool CaseEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (Lower(a[i]) != Lower(b[i]))
                return false;
        return true;

        static int Lower(byte c) => c >= 'A' && c <= 'Z' ? c + ('a' - 'A') : c;
    }

    /// <summary>Cvar_Command: handles variable inspection and changing from the console.</summary>
    private bool CvarCommand()
    {
        byte[] name = Z(_argv[0]);
        fixed (byte* n = name)
        {
            if (!_cvars.Exists(n))
                return false;
            // perform a variable print or set
            if (_argv.Count == 1)
            {
                Msg("\""u8).Msg(_argv[0]).Msg("\" is \""u8).Msg(_cvars.VariableString(n)).Msg("\"\n"u8).End();
                return true;
            }
            fixed (byte* v = Z(_argv[1]))
                _cvars.Set(n, v);
        }
        return true;
    }

    /// <summary>A zero-terminated copy.</summary>
    public static byte[] Z(ReadOnlySpan<byte> s)
    {
        var z = new byte[s.Length + 1];
        s.CopyTo(z);
        return z;
    }

    // ---- console output

    /// <summary>Con_Printf of a text without conversions.</summary>
    public void Printf(ReadOnlySpan<byte> text) => Con?.Printf(text);

    /// <summary>A Con_Printf message: <see cref="Message.Msg(ReadOnlySpan{byte})"/> ... <see cref="Message.End"/>.</summary>
    public Message Msg(ReadOnlySpan<byte> text) => new Message(Con).Msg(text);

    /// <summary>A Con_Printf being built; nothing is printed without a console.</summary>
    public readonly struct Message(IConsole? con)
    {
        public Message Msg(ReadOnlySpan<byte> text)
        {
            con?.Msg(text);
            return this;
        }

        public Message Msg(byte* s)
        {
            con?.Msg(s);
            return this;
        }

        public Message Int(int value)
        {
            con?.MsgInt(value);
            return this;
        }

        public void End() => con?.EndMsg();
    }

    // ---- cmd.c's own commands

    /// <summary>
    /// Cmd_StuffCmds_f: adds command line parameters as script statements. Commands lead with a +, and continue
    /// until a - or another +.
    /// </summary>
    private void StuffCmds()
    {
        if (Argc != 1)
        {
            Printf("stuffcmds : execute command line parameters\n"u8);
            return;
        }
        // build the combined string to parse from
        string text = string.Join(" ", CommandLine);
        if (text.Length == 0)
            return;
        var build = new StringBuilder();
        for (int i = 0; i < text.Length; i++)      // C: i < s-1, the joined text's length
        {
            if (text[i] != '+')
                continue;
            i++;
            int j = i;
            while (j < text.Length && text[j] != '+' && text[j] != '-')
                j++;
            build.Append(text, i, j - i).Append('\n');
            i = j - 1;
        }
        if (build.Length != 0)
            InsertText(Encoding.Latin1.GetBytes(build.ToString()));
    }

    /// <summary>Cmd_Exec_f: the script's text (to its first NUL, as COM_LoadHunkFile's terminator ends it) inserted.</summary>
    private void Exec()
    {
        if (Argc != 2)
        {
            Printf("exec <filename> : execute a script file\n"u8);
            return;
        }
        if (_files.Find(Argv(1)) is not { } f)
        {
            Msg("couldn't exec "u8).Msg(Argv(1)).Msg("\n"u8).End();
            return;
        }
        Msg("execing "u8).Msg(Argv(1)).Msg("\n"u8).End();
        ReadOnlySpan<byte> s = f.Span;
        int nul = s.IndexOf((byte)0);
        InsertText(nul < 0 ? s : s[..nul]);
    }

    /// <summary>Cmd_Echo_f: just prints the rest of the line to the console.</summary>
    private void Echo()
    {
        for (int i = 1; i < Argc; i++)
            Msg(Argv(i)).Msg(" "u8).End();
        Printf("\n"u8);
    }

    /// <summary>Cmd_Alias_f: creates a new command that executes a command string (possibly ; seperated).</summary>
    private void Alias()
    {
        if (Argc == 1)
        {
            Printf("Current alias commands:\n"u8);
            foreach ((byte[] name, byte[] value) in _aliases)
                Msg(name).Msg(" : "u8).Msg(value).Msg("\n"u8).End();
            return;
        }
        byte[] s = Argv(1);
        if (s.Length >= MaxAliasName)
        {
            Printf("Alias name is too long\n"u8);
            return;
        }
        // copy the rest of the command line (each argument and a space: C's "i != c" is always true)
        var cmd = new List<byte>();
        for (int i = 2; i < Argc; i++)
        {
            cmd.AddRange(Argv(i));
            cmd.Add((byte)' ');
        }
        cmd.Add((byte)'\n');
        // if the alias allready exists, reuse it
        for (int i = 0; i < _aliases.Count; i++)
            if (_aliases[i].Name.AsSpan().SequenceEqual(s))
            {
                _aliases[i] = (s, cmd.ToArray());
                return;
            }
        _aliases.Insert(0, (s, cmd.ToArray()));
    }
}
