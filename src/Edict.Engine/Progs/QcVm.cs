// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using Edict.Exact;

namespace Edict.Engine;

/// <summary>A Host_Error in the reference: the frame is abandoned (PR_RunError ends in one).</summary>
public sealed class HostErrorException(string message) : Exception(message);

/// <summary>
/// What the VM needs from the engine around it: the builtins, string contents, the server state, and a capture point.
/// A struct type parameter of <see cref="QcVm.Execute"/>, so the calls are direct (docs/PLAN.md B.4).
/// </summary>
public unsafe interface IProgsHost
{
    /// <summary>
    /// <c>pr_builtins[num] ()</c>. The host owns the VM: a builtin reads its arguments (<see cref="QcVm.Argc"/>,
    /// the parm globals) and may run QuakeC itself through it.
    /// </summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    void Builtin(int num);

    /// <summary><c>pr_strings + ofs</c>: a zero-terminated string.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    byte* String(int ofs);

    /// <summary><c>sv.state == ss_active</c>.</summary>
    [NoAlloc(Constraints.All)]
    bool Active { get; }

    /// <summary>OP_CALL of a QuakeC function (the capture point EDICT_CAPTURE_CALL); nothing to do in the engine.</summary>
    [NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = QcVm.ErrorReason)]
    void Called(int fnum);
}

/// <summary>
/// The QuakeC interpreter, pr_exec.c: <c>PR_ExecuteProgram</c>, <c>PR_EnterFunction</c>, <c>PR_LeaveFunction</c>,
/// with C's statement semantics, operand order and float/double types. Globals and edicts are native memory owned
/// by the caller; edicts are laid out with the reference's stride (docs/adr/D7), so entity values and field pointers
/// are byte offsets from edict 0 exactly as in C. Not ported: the per-function profile counter (console only) and
/// statement tracing.
/// </summary>
[NoAlloc(Constraints.All, Allow = Allow.Throw, Reason = ErrorReason)]
public sealed unsafe class QcVm
{
    public const string ErrorReason = "PR_RunError/Host_Error and Sys_Error, fatal for the frame or the server as in C";

    public const int MaxStackDepth = 32;
    public const int LocalStackSize = 2048;
    public const int OfsReturn = 1;
    public const int OfsParm0 = 4;

    private static readonly HostErrorException NullFunctionAtEntry = new("PR_ExecuteProgram: NULL function");
    private static readonly HostErrorException RunawayLoop = new("runaway loop error");
    private static readonly HostErrorException NullFunction = new("NULL function");
    private static readonly HostErrorException BadBuiltin = new("Bad builtin call number");
    private static readonly HostErrorException WorldAssignment = new("assignment to world entity");
    private static readonly HostErrorException StackOverflow = new("stack overflow");
    private static readonly HostErrorException LocalsOverflow = new("PR_ExecuteProgram: locals stack overflow");
    private static readonly HostErrorException LocalsUnderflow = new("PR_ExecuteProgram: locals stack underflow");
    private static readonly HostErrorException BadOpcode = new("Bad opcode");
    private static readonly SysErrorException StackUnderflow = new("prog stack underflow");

    private struct StackEntry
    {
        public int S;
        public DFunction* F;
    }

    [InlineArray(MaxStackDepth)]
    private struct Stack
    {
        private StackEntry _element0;
    }

    [InlineArray(LocalStackSize)]
    private struct LocalStack
    {
        private int _element0;
    }

    private readonly Progs _progs;
    private readonly DStatement* _statements;
    private readonly DFunction* _functions;
    private readonly int _numFunctions;
    private readonly int _numBuiltins;
    private readonly int* _globals;
    private readonly byte* _edicts;
    private readonly int _fieldsOffset;       // sizeof (edict_t) - sizeof (entvars_t) of the reference (D7)

    private Stack _stack;
    private LocalStack _localStack;

    /// <summary>pr_depth.</summary>
    public int Depth { get; private set; }

    /// <summary>pr_argc: the argument count of the builtin being called.</summary>
    public int Argc { get; private set; }

    /// <summary>pr_xstatement.</summary>
    public int XStatement { get; private set; }

    private int _localStackUsed;
    private DFunction* _xfunction;

    /// <param name="progs">The loaded progs: statements and functions are read from it.</param>
    /// <param name="globals">pr_globals: <c>progs.NumGlobals</c> words.</param>
    /// <param name="edicts">sv.edicts: edict 0 of an area with the reference's stride.</param>
    /// <param name="edictHeaderBytes">The reference's <c>sizeof (edict_t) - sizeof (entvars_t)</c>.</param>
    /// <param name="numBuiltins">pr_numbuiltins.</param>
    [NoAllocUnchecked("map load: reads the loaded progs once")]
    public QcVm(Progs progs, int* globals, byte* edicts, int edictHeaderBytes, int numBuiltins)
    {
        _progs = progs;
        _statements = progs.Statements;
        _functions = progs.Functions;
        _numFunctions = progs.NumFunctions;
        _globals = globals;
        _edicts = edicts;
        _fieldsOffset = edictHeaderBytes;
        _numBuiltins = numBuiltins;
    }

    public Progs Progs => _progs;

    private float* F(int ofs) => (float*)(_globals + ofs);

    /// <summary><c>(int *)&amp;PROG_TO_EDICT (e)->v</c>: the first field word of the edict at byte offset e.</summary>
    private int* Fields(int e) => (int*)(_edicts + e + _fieldsOffset);

    /// <summary>PR_EnterFunction: returns the new program statement counter.</summary>
    private int EnterFunction(DFunction* f)
    {
        _stack[Depth].S = XStatement;
        _stack[Depth].F = _xfunction;
        Depth++;
        if (Depth >= MaxStackDepth)
            throw RunError(StackOverflow);

        // save off any locals that the new function steps on
        int c = f->Locals;
        if (_localStackUsed + c > LocalStackSize)
            throw RunError(LocalsOverflow);

        for (int i = 0; i < c; i++)
            _localStack[_localStackUsed + i] = _globals[f->ParmStart + i];
        _localStackUsed += c;

        // copy parameters
        int o = f->ParmStart;
        for (int i = 0; i < f->NumParms; i++)
        {
            for (int j = 0; j < f->ParmSize[i]; j++)
            {
                _globals[o] = _globals[OfsParm0 + i * 3 + j];
                o++;
            }
        }

        _xfunction = f;
        return f->FirstStatement - 1;   // offset the s++
    }

    /// <summary>PR_LeaveFunction.</summary>
    private int LeaveFunction()
    {
        if (Depth <= 0)
            throw StackUnderflow;

        // restore locals from the stack
        int c = _xfunction->Locals;
        _localStackUsed -= c;
        if (_localStackUsed < 0)
            throw RunError(LocalsUnderflow);

        for (int i = 0; i < c; i++)
            _globals[_xfunction->ParmStart + i] = _localStack[_localStackUsed + i];

        // up stack
        Depth--;
        _xfunction = _stack[Depth].F;
        return _stack[Depth].S;
    }

    /// <summary>
    /// PR_RunError: "pr_depth = 0; // dump the stack so host_error can shutdown functions", then Host_Error. As in C,
    /// localstack_used is left as it is.
    /// </summary>
    public HostErrorException RunError(HostErrorException e)
    {
        Depth = 0;
        return e;
    }

    /// <summary>PR_ExecuteProgram: the interpretation main loop.</summary>
    public void Execute<THost>(ref THost host, int fnum) where THost : struct, IProgsHost
    {
        if (fnum == 0 || fnum >= _numFunctions)
            throw NullFunctionAtEntry;      // C prints the self edict first (ED_Print): console output only

        DFunction* f = _functions + fnum;
        int runaway = 100000;

        // make a stack frame
        int exitdepth = Depth;

        int s = EnterFunction(f);

        while (true)
        {
            s++;    // next statement

            DStatement* st = _statements + s;
            int* a = _globals + st->A;
            int* b = _globals + st->B;
            int* c = _globals + st->C;

            if (--runaway == 0)
                throw RunError(RunawayLoop);

            XStatement = s;

            switch (st->Op)
            {
                case Op.AddF:
                    *(float*)c = *(float*)a + *(float*)b;
                    break;
                case Op.AddV:
                    ((float*)c)[0] = ((float*)a)[0] + ((float*)b)[0];
                    ((float*)c)[1] = ((float*)a)[1] + ((float*)b)[1];
                    ((float*)c)[2] = ((float*)a)[2] + ((float*)b)[2];
                    break;

                case Op.SubF:
                    *(float*)c = *(float*)a - *(float*)b;
                    break;
                case Op.SubV:
                    ((float*)c)[0] = ((float*)a)[0] - ((float*)b)[0];
                    ((float*)c)[1] = ((float*)a)[1] - ((float*)b)[1];
                    ((float*)c)[2] = ((float*)a)[2] - ((float*)b)[2];
                    break;

                case Op.MulF:
                    *(float*)c = *(float*)a * *(float*)b;
                    break;
                case Op.MulV:
                    *(float*)c = ((float*)a)[0] * ((float*)b)[0]
                               + ((float*)a)[1] * ((float*)b)[1]
                               + ((float*)a)[2] * ((float*)b)[2];
                    break;
                case Op.MulFV:
                    ((float*)c)[0] = *(float*)a * ((float*)b)[0];
                    ((float*)c)[1] = *(float*)a * ((float*)b)[1];
                    ((float*)c)[2] = *(float*)a * ((float*)b)[2];
                    break;
                case Op.MulVF:
                    ((float*)c)[0] = *(float*)b * ((float*)a)[0];
                    ((float*)c)[1] = *(float*)b * ((float*)a)[1];
                    ((float*)c)[2] = *(float*)b * ((float*)a)[2];
                    break;

                case Op.DivF:
                    *(float*)c = *(float*)a / *(float*)b;
                    break;

                case Op.BitAnd:
                    *(float*)c = CCast.I32(*(float*)a) & CCast.I32(*(float*)b);
                    break;

                case Op.BitOr:
                    *(float*)c = CCast.I32(*(float*)a) | CCast.I32(*(float*)b);
                    break;

                case Op.Ge:
                    *(float*)c = *(float*)a >= *(float*)b ? 1 : 0;
                    break;
                case Op.Le:
                    *(float*)c = *(float*)a <= *(float*)b ? 1 : 0;
                    break;
                case Op.Gt:
                    *(float*)c = *(float*)a > *(float*)b ? 1 : 0;
                    break;
                case Op.Lt:
                    *(float*)c = *(float*)a < *(float*)b ? 1 : 0;
                    break;
                case Op.And:
                    *(float*)c = *(float*)a != 0 && *(float*)b != 0 ? 1 : 0;
                    break;
                case Op.Or:
                    *(float*)c = *(float*)a != 0 || *(float*)b != 0 ? 1 : 0;
                    break;

                case Op.NotF:
                    *(float*)c = *(float*)a == 0 ? 1 : 0;
                    break;
                case Op.NotV:
                    *(float*)c = ((float*)a)[0] == 0 && ((float*)a)[1] == 0 && ((float*)a)[2] == 0 ? 1 : 0;
                    break;
                case Op.NotS:
                    *(float*)c = *a == 0 || *host.String(*a) == 0 ? 1 : 0;
                    break;
                case Op.NotFnc:
                    *(float*)c = *a == 0 ? 1 : 0;
                    break;
                case Op.NotEnt:
                    *(float*)c = *a == 0 ? 1 : 0;   // PROG_TO_EDICT (a->edict) == sv.edicts
                    break;

                case Op.EqF:
                    *(float*)c = *(float*)a == *(float*)b ? 1 : 0;
                    break;
                case Op.EqV:
                    *(float*)c = ((float*)a)[0] == ((float*)b)[0]
                              && ((float*)a)[1] == ((float*)b)[1]
                              && ((float*)a)[2] == ((float*)b)[2] ? 1 : 0;
                    break;
                case Op.EqS:
                    *(float*)c = CString.Strcmp(host.String(*a), host.String(*b)) == 0 ? 1 : 0;
                    break;
                case Op.EqE:
                case Op.EqFnc:
                    *(float*)c = *a == *b ? 1 : 0;
                    break;

                case Op.NeF:
                    *(float*)c = *(float*)a != *(float*)b ? 1 : 0;
                    break;
                case Op.NeV:
                    *(float*)c = ((float*)a)[0] != ((float*)b)[0]
                              || ((float*)a)[1] != ((float*)b)[1]
                              || ((float*)a)[2] != ((float*)b)[2] ? 1 : 0;
                    break;
                case Op.NeS:
                    *(float*)c = CString.Strcmp(host.String(*a), host.String(*b));   // the magnitude is kept
                    break;
                case Op.NeE:
                case Op.NeFnc:
                    *(float*)c = *a != *b ? 1 : 0;
                    break;

                // ==================
                case Op.StoreF:
                case Op.StoreEnt:
                case Op.StoreFld:       // integers
                case Op.StoreS:
                case Op.StoreFnc:       // pointers
                    *b = *a;
                    break;
                case Op.StoreV:         // as words: a float copy keeps the bits too, words say so
                    b[0] = a[0];
                    b[1] = a[1];
                    b[2] = a[2];
                    break;

                case Op.StorePF:
                case Op.StorePEnt:
                case Op.StorePFld:      // integers
                case Op.StorePS:
                case Op.StorePFnc:      // pointers
                    *(int*)(_edicts + *b) = *a;
                    break;
                case Op.StorePV:
                {
                    int* ptr = (int*)(_edicts + *b);
                    ptr[0] = a[0];
                    ptr[1] = a[1];
                    ptr[2] = a[2];
                    break;
                }

                case Op.Address:
                    if (*a == 0 && host.Active)
                        throw RunError(WorldAssignment);
                    *c = (int)((byte*)(Fields(*a) + *b) - _edicts);
                    break;

                case Op.LoadF:
                case Op.LoadFld:
                case Op.LoadEnt:
                case Op.LoadS:
                case Op.LoadFnc:
                    *c = Fields(*a)[*b];
                    break;

                case Op.LoadV:
                {
                    int* field = Fields(*a) + *b;
                    c[0] = field[0];
                    c[1] = field[1];
                    c[2] = field[2];
                    break;
                }

                // ==================
                case Op.IfNot:
                    if (*a == 0)
                        s += st->B - 1;     // offset the s++
                    break;

                case Op.If:
                    if (*a != 0)
                        s += st->B - 1;     // offset the s++
                    break;

                case Op.Goto:
                    s += st->A - 1;         // offset the s++
                    break;

                case Op.Call0:
                case Op.Call1:
                case Op.Call2:
                case Op.Call3:
                case Op.Call4:
                case Op.Call5:
                case Op.Call6:
                case Op.Call7:
                case Op.Call8:
                {
                    Argc = st->Op - Op.Call0;
                    if (*a == 0)
                        throw RunError(NullFunction);

                    DFunction* newf = _functions + *a;

                    if (newf->FirstStatement < 0)
                    {   // negative statements are built in functions
                        int i = -newf->FirstStatement;
                        if (i >= _numBuiltins)
                            throw RunError(BadBuiltin);
                        host.Builtin(i);
                        break;
                    }

                    host.Called(*a);
                    s = EnterFunction(newf);
                    break;
                }

                case Op.Done:
                case Op.Return:
                    _globals[OfsReturn] = a[0];
                    _globals[OfsReturn + 1] = a[1];
                    _globals[OfsReturn + 2] = a[2];

                    s = LeaveFunction();
                    if (Depth == exitdepth)
                        return;             // all done
                    break;

                case Op.State:
                {
                    int* ed = Fields(_globals[GlobalVars.Self]);
                    // nextthink = time + 0.1: the double literal makes it a double sum, stored to float
                    *(float*)(ed + EntVars.Nextthink) = (float)(*F(GlobalVars.Time) + 0.1);
                    if (*(float*)a != *(float*)(ed + EntVars.Frame))
                        *(float*)(ed + EntVars.Frame) = *(float*)a;
                    ed[EntVars.Think] = *b;
                    break;
                }

                default:
                    throw RunError(BadOpcode);
            }
        }
    }
}
