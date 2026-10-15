// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Edict.Engine;

// The progs.dat format (pr_comp.h), shared by quake and qcc. Little-endian, read in place after one copy.

/// <summary><c>etype_t</c>.</summary>
public enum EType : ushort
{
    Void, String, Float, Vector, Entity, Field, Function, Pointer,
}

/// <summary>The opcodes of pr_comp.h, in its order.</summary>
public enum Op : ushort
{
    Done,
    MulF, MulV, MulFV, MulVF,
    DivF,
    AddF, AddV,
    SubF, SubV,
    EqF, EqV, EqS, EqE, EqFnc,
    NeF, NeV, NeS, NeE, NeFnc,
    Le, Ge, Lt, Gt,
    LoadF, LoadV, LoadS, LoadEnt, LoadFld, LoadFnc,
    Address,
    StoreF, StoreV, StoreS, StoreEnt, StoreFld, StoreFnc,
    StorePF, StorePV, StorePS, StorePEnt, StorePFld, StorePFnc,
    Return,
    NotF, NotV, NotS, NotEnt, NotFnc,
    If, IfNot,
    Call0, Call1, Call2, Call3, Call4, Call5, Call6, Call7, Call8,
    State,
    Goto,
    And, Or,
    BitAnd, BitOr,
}

/// <summary><c>dstatement_t</c>. The operands are signed, as in C.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DStatement
{
    public Op Op;
    public short A, B, C;
}

/// <summary><c>ddef_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DDef
{
    public const ushort SaveGlobal = 1 << 15;     // DEF_SAVEGLOBAL: the variable needs to be saved in savegames

    public ushort Type;
    public ushort Ofs;
    public int SName;
}

/// <summary><c>dfunction_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DFunction
{
    public const int MaxParms = 8;

    public int FirstStatement;                        // negative numbers are builtins
    public int ParmStart;
    public int Locals;                                // total ints of parms + locals
    public int Profile;                               // runtime
    public int SName;
    public int SFile;                                 // source file defined in
    public int NumParms;
    public fixed byte ParmSize[MaxParms];
}

/// <summary><c>dprograms_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DPrograms
{
    public const int Version = 6;
    /// <summary>PROGHEADER_CRC (progdefs.q1): the system variables the engine and the progs agree on.</summary>
    public const int HeaderCrc = 5927;

    public int VersionNumber;
    public int Crc;                                   // check of header file
    public int OfsStatements, NumStatements;          // statement 0 is an error
    public int OfsGlobalDefs, NumGlobalDefs;
    public int OfsFieldDefs, NumFieldDefs;
    public int OfsFunctions, NumFunctions;            // function 0 is an empty
    public int OfsStrings, NumStrings;                // first string is a null string
    public int OfsGlobals, NumGlobals;
    public int EntityFields;
}

/// <summary>
/// <c>PR_LoadProgs</c> (pr_edict.c): progs.dat copied once into native memory (C loads it into the hunk), the CRC
/// over the file, the header checks, and pointers to its lumps. The globals in the copy are the initial values;
/// a VM runs on its own globals (<see cref="QcVm"/>). Load path only; errors that are Sys_Error in C throw.
/// </summary>
public sealed unsafe class Progs : IDisposable
{
    private byte* _data;

    public int Length { get; }

    /// <summary><c>pr_crc</c>: CRC-16 of the whole file.</summary>
    public ushort FileCrc { get; }

    public DPrograms* Header => (DPrograms*)_data;
    public DStatement* Statements { get; }
    public DFunction* Functions { get; }
    public DDef* GlobalDefs { get; }
    public DDef* FieldDefs { get; }
    public byte* Strings { get; }
    public int* InitialGlobals { get; }

    public int NumStatements => Header->NumStatements;
    public int NumFunctions => Header->NumFunctions;
    public int NumGlobals => Header->NumGlobals;
    public int NumStrings => Header->NumStrings;
    public int EntityFields => Header->EntityFields;

    public Progs(QFile file)
    {
        if (file.Length < sizeof(DPrograms))
            throw new InvalidDataException("progs.dat is too short");
        Length = file.Length;
        _data = (byte*)NativeMemory.AlignedAlloc((nuint)Length, 64);
        try
        {
            file.Span.CopyTo(new Span<byte>(_data, Length));
            FileCrc = Crc.Of(file.Span);

            DPrograms* h = Header;
            if (h->VersionNumber != DPrograms.Version)
                throw new InvalidDataException(
                    $"progs.dat has wrong version number ({h->VersionNumber} should be {DPrograms.Version})");
            if (h->Crc != DPrograms.HeaderCrc)
                throw new InvalidDataException("progs.dat system vars have been modified, progdefs.h is out of date");

            Statements = (DStatement*)Lump(h->OfsStatements, h->NumStatements, sizeof(DStatement));
            Functions = (DFunction*)Lump(h->OfsFunctions, h->NumFunctions, sizeof(DFunction));
            GlobalDefs = (DDef*)Lump(h->OfsGlobalDefs, h->NumGlobalDefs, sizeof(DDef));
            FieldDefs = (DDef*)Lump(h->OfsFieldDefs, h->NumFieldDefs, sizeof(DDef));
            Strings = Lump(h->OfsStrings, h->NumStrings, 1);
            InitialGlobals = (int*)Lump(h->OfsGlobals, h->NumGlobals, 4);
            if (h->EntityFields < 0)
                throw new InvalidDataException("progs.dat: negative entityfields");

            for (int i = 0; i < h->NumFieldDefs; i++)
                if ((FieldDefs[i].Type & DDef.SaveGlobal) != 0)
                    throw new InvalidDataException("PR_LoadProgs: pr_fielddefs[i].type & DEF_SAVEGLOBAL");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Unlike C, lumps outside the file are an error.
    private byte* Lump(int ofs, int count, int size)
    {
        if (ofs < 0 || count < 0 || ofs + (long)count * size > Length)
            throw new InvalidDataException("progs.dat: a lump lies outside the file");
        return _data + ofs;
    }

    /// <summary>A string of the progs string block, up to its zero byte.</summary>
    public ReadOnlySpan<byte> String(int ofs) => MemoryMarshal.CreateReadOnlySpanFromNullTerminated(Strings + ofs);

    public void Dispose()
    {
        if (_data is not null)
        {
            NativeMemory.AlignedFree(_data);
            _data = null;
        }
    }
}
