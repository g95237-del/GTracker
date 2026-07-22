# EDI Integration Studio v0.1.0-alpha.1

This is an experimental Windows x64 alpha of GTracker / EDI Integration Studio.

## Run The Studio

1. Extract the entire ZIP to a writable folder.
2. Keep all DLLs and the `RuntimePackages` folder beside `GTracker.App.exe`.
3. Run `GTracker.App.exe`.

The application is self-contained and does not require a separate .NET runtime. Generating and building Unity integration plugins still requires the .NET 8 SDK and any targeting pack required by the detected Unity game.

Back up integration projects before testing. Captured `.ediclip` files contain screen pixels and may include notifications or overlapping windows. Projects, generated mod files, telemetry, and manifests may contain local paths.

Documentation and issue tracker: <https://github.com/g95237-del/GTracker>
