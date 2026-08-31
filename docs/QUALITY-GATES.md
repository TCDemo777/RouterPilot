# RouterPilot quality gates

These checks are intentionally scoped to new or changed code. Existing
framework-bound WPF patterns and known dependency/installer warnings remain
visible and are not blanket-suppressed.

Run `powershell -ExecutionPolicy Bypass -File .\scripts\quality-gates.ps1`
before committing. The script checks changed C# files for silent catches,
blocking `.Result`/`.Wait()` calls, and non-event `async void` declarations.

RouterPilot-specific rules:

- Preserve behavior before pursuing line-count reductions; RouterManager is a
  compatibility and lifecycle facade where stateful ownership is appropriate.
- `async void` is limited to WPF event handlers and framework callbacks;
  application and service methods return `Task`/`Task<T>`.
- Keep cancellation distinct from failures. Do not silently swallow
  non-cancellation exceptions.
- Intentional fire-and-forget tasks must observe faults and expected
  cancellation.
- Prefer stateless parsers and keep the active router/profile context
  authoritative.
- Do not create competing caches or synchronization gates.
- Protocol changes require explicit behavioral tests.
- Existing NU1701 and WiX ICE61/WIX1076 warnings remain reported; do not hide
  them with broad `NoWarn` settings.
