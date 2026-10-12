; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NA001 | NoAlloc | Error | Allocation
NA002 | NoAlloc | Error | Boxing or closure capture
NA003 | NoAlloc | Error | Lock, spin or wait
NA004 | NoAlloc | Error | Blocking call
NA005 | NoAlloc | Error | Async
NA006 | NoAlloc | Error | Explicit throw
NA007 | NoAlloc | Error | Reflection or dynamic
NA008 | NoAlloc | Error | Callee without contract
NA009 | NoAlloc | Error | Relaxation without reason
NA010 | NoAlloc | Error | Override relaxes an inherited marker
NA011 | NoAlloc | Error | Possible type initialization
NA012 | NoAlloc | Error | Pool rent
NA013 | NoAlloc | Error | Callee relaxed (contract breach)
NA014 | NoAlloc | Warning | Allowlist entry does not resolve
NA015 | NoAlloc | Warning | Allowlist entry is malformed
