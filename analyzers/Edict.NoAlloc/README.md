# NoAlloc Analyzer

A Roslyn analyzer that verifies at compile time what code marked `[NoAlloc]` promises: no managed heap
allocation, and optionally no locks, blocking, async, explicit throws, reflection or type initialization; and that
it only calls members that promise the same. It is a compile-time guarantee for steady-state hot paths, not a
worst-case execution time guarantee; collector pauses caused elsewhere are out of scope. Because every callee of a
marked member must itself carry a matching marker or an allowlist entry (NA008), the promise composes member by
member through the call graph.

## Origin and license

Copied from the author's project Prism (`analyzers/hotpath`, commit `6e60e5aba15f467327c8b1ef1665e3e9136e9ad7`,
2026-10-12): the analyzer, the annotations and the analyzer tests. Renamed from `Archfacts.HotPath` to
`Edict.NoAlloc`, diagnostics from `HP0xx` to `NA0xx`; the attribute namespace is `Edict.NoAlloc`. Prism's
transitive verifier and benchmarks were not copied. The original is Apache-2.0; its sole author licenses this
copy under the GNU GPL, version 2 or later (`SPDX-License-Identifier: GPL-2.0-or-later`), like the rest of Edict.
From here on the copy evolves independently.

## Use in Edict

```xml
<Import Project="$(RepoRoot)analyzers/Edict.NoAlloc/Edict.NoAlloc.Analyzers/build/Edict.NoAlloc.Analyzers.props" />
<ItemGroup>
  <ProjectReference Include="$(RepoRoot)analyzers/Edict.NoAlloc/Edict.NoAlloc.Analyzers/Edict.NoAlloc.Analyzers.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

Code inside `namespace Edict.*` needs `using Edict.NoAlloc;` or `global using Edict.NoAlloc;` in any file of the
project. Without it, `[NoAlloc]` binds to the namespace `Edict.NoAlloc` and fails with CS0616 ("is not an attribute
class"); checked on SDK 10.0.203.

## Attributes

```csharp
[NoAlloc]                                                        // NoAlloc
[NoAlloc(Constraints.NoLock | Constraints.NoBlock)]              // NoAlloc and the named constraints
[NoAlloc(Constraints.All)]                                       // all constraints
[NoAlloc(Constraints.All, Allow = Allow.Spin, Reason = "...")]   // Spin lifts spin operations only
[NoAllocUnchecked("...")]                                        // not verified; promises nothing
```

| Item | Meaning |
| --- | --- |
| `[NoAlloc(constraints)]` | On a method, constructor, property, local function, class, struct or interface. `NoAlloc` is always included. On a type it covers every member declared in it, not nested types; a member's own marker wins. |
| `Allow` + `Reason` | Lifts named constraints for the section (`Allow.Spin`: spin waits and spin locks only). The section promises only what remains; stricter callers get NA013. |
| `[NoAllocUnchecked(reason)]` | Takes a member or type out of verification. The gap stays visible: the member promises nothing, callers that require a constraint get NA013. |
| Inheritance | Overrides and interface implementations inherit the marker of what they override or implement and are verified under it without a marker of their own; a marker, `Allow` or `[NoAllocUnchecked]` that drops an inherited constraint is NA010. |

The types live in namespace `Edict.NoAlloc`. The analyzer package injects internal, embedded copies into every
compilation (no runtime dependency, no conflicts through `InternalsVisibleTo`). To share public types instead,
reference `Edict.NoAlloc.Annotations` and set `<EdictNoAllocInjectAttributes>false</EdictNoAllocInjectAttributes>`.
The analyzer recognizes either copy by name, also on members of referenced assemblies.

## Constraints and rules

| Constraint | Checked in the section | Diagnostic |
| --- | --- | --- |
| `NoAlloc` | `new` of reference types, arrays, non-empty `params` arrays, string concatenation and interpolation, collection expressions not targeting `Span`/`ReadOnlySpan`, `with` on a record class, anonymous objects, `new T()` without a struct constraint, array range copies, `dynamic`, iterators, async state machines, delegate creation from lambdas and method groups | NA001 |
| `NoAlloc` | boxing, including type patterns that test a value against a reference type; lambdas that capture | NA002 |
| `NoAlloc` | `ArrayPool<T>.Rent`, `MemoryPool<T>.Rent` | NA012 |
| `NoLock` | `lock`, `Monitor`, `Lock`, `Mutex`, semaphore and reader-writer locks, `SpinLock`, `SpinWait`, `Thread.SpinWait` | NA003 |
| `NoBlock` | `Thread.Sleep/Join`, waits on tasks, events, wait handles and barriers, `Task<T>.Result`, awaiter `GetResult`, `BlockingCollection<T>`, synchronous stream, file, console, text reader/writer and socket I/O | NA004 |
| `NoAsync` | `async` methods and local functions, once at the declaration | NA005 |
| `NoThrow` | explicit `throw` (the thrown object is not reported separately) | NA006 |
| `NoDynamic` | `System.Reflection`, `Type.GetMethod` and its relatives, `Activator`, `Delegate.DynamicInvoke`, `dynamic` | NA007 |
| `NoInit` | access to a static member, or construction, of a source type with an explicit static constructor, outside that type | NA011 |
| every | a callee that does not promise a required constraint: methods, constructors, accessors, operators, conversions, `foreach` enumerator members (one finding per loop), `Dispose` via `using`, `Deconstruct`, event accessors; delegate and function pointer invocations (the target is unknown) | NA008 |
| every | a callee relaxed by `Allow` or `[NoAllocUnchecked]` for a constraint the caller requires (contract breach) | NA013 |

A constructor without an initializer is checked for the base class's parameterless constructor it calls
implicitly, at the constructor's name.

NA009 reports `Allow` without `Reason` and `[NoAllocUnchecked]` without a reason; NA010 an override or
implementation that relaxes an inherited marker (on the type when a base class provides the implementation);
NA014 and NA015 (warnings) allowlist entries that do not resolve or are malformed. All others are errors. NA001–
NA007, NA011 and NA012 prove an operation; NA008 and NA013 report a missing promise.

Callees promise every constraint without a marker when they are intrinsic (`Math`, `MathF`, `BitOperations`,
`Unsafe`, `Interlocked`, `Volatile`), implicit parameterless struct constructors or non-virtual auto-property
accessors. Always allowed: constant expressions, `nameof`, `stackalloc`, tuples, `try`/`catch`/`finally`.

Scopes: local functions without a marker take the contract of the method they are declared in; a marked local
function is verified anywhere, also inside a lambda. Lambda bodies are never verified (the lambda itself is the
delegate creation). Instance field and property initializers run under the contracts of the type's marker and
its verified constructors. Generated code is skipped. NoInit covers source types only: Roslyn does not expose
`beforefieldinit` for referenced types, so their static constructors are not checked.

## Allowlist

`noalloc-allowlist.toml` (or `*.noalloc-allowlist.toml`) as `AdditionalFiles`, one `[[member]]` table per entry.
`symbol` is `cs:<assembly>/<documentation comment id>`:

```toml
[[member]]
symbol = "cs:*/M:System.Span`1.Slice(System.Int32,System.Int32)"   # canonical symbol; T: covers every member of a type
constraints = ["All"]                                               # or NoAlloc, NoLock, NoBlock, NoAsync, NoThrow, NoDynamic, NoInit
runtime = "*"                                                       # runtime library version (10.0, 8.0, …) or * for any
reason = "returns a span over existing memory"
```

- An entry is an assumed contract for the listed constraints only; a caller that requires more gets NA008.
- The assembly part may be `*` for runtime library members, which reference assemblies place differently per
  target framework. `runtime` is compared with the version of the assembly that defines `System.Object`.
- A baseline for the BCL is embedded in the analyzer (`Allowlists/bcl.noalloc-allowlist.toml`); entries a
  target framework lacks are skipped. Project files add to it.
- Symbols resolve through Roslyn's `DocumentationCommentId`, so overloads are exact and generic entries cover all
  instantiations. Indexers renamed with `[IndexerName]` do not resolve as `P:` ids; list their accessor.

## Design

- **No cost without use.** At compilation start the analyzer looks up the marker types; if neither exists it
  registers nothing.
- **Cheap for unmarked code.** A method's contract is computed from cached markers; only overrides, explicit
  implementations and public instance methods of types with a marked interface can inherit one, and only
  methods with a contract are cached. NA010 runs in the symbol action Roslyn already calls per method.
- **One walk per body.** Each verified body is walked once by an `OperationWalker`; there are no per-operation
  callbacks for unmarked code. Marked local functions outside verified code are found with a syntax filter on
  local functions with attribute lists.
- **Concurrent.** The analyzer enables concurrent execution; all caches are concurrent dictionaries whose
  factories are allocated once, and answers do not depend on the order symbols are analyzed in.
- **No strings for runtime types.** Forbidden and intrinsic runtime types are matched by metadata name and
  namespace at the callee and cached per callee, instead of resolving every type through every reference.

Benchmark of the original in Prism (5,000 methods, analyzer execution time from Roslyn telemetry, single
threaded, best of ten runs; not re-measured after the rename): about 2 ms per 1,000 methods and 5 % of binding time for unmarked code, about
2.5 ms per 1,000 methods for code with marked methods and interface contracts. With violations the time is
dominated by Roslyn's handling of each reported diagnostic, not by the analyzer.

## Not covered yet

Primary constructors (no tests cover them); delegates and function
pointers with a statically known target; code fixes; a runtime allocation assertion helper;
allocation evidence per baseline allowlist entry; implicit runtime exceptions (bounds, null, overflow); static
constructors of referenced types.
