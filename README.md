# NcfDesktop
NeuCharFramework Desktop GUI

## iPhone client

The repository now also contains an independent, remote-only iPhone client:

- `src/NcfMobileApp.iOS` — native .NET for iOS/UIKit application.
- `src/NcfMobileApp.Core` — HTTPS AdminChat client shared independently from the desktop UI.
- `src/NcfMobileApp.Core.Tests` — endpoint, authentication, and streaming protocol tests.

See [`src/NcfMobileApp.iOS/README.md`](src/NcfMobileApp.iOS/README.md) for architecture,
provisioning, and device installation instructions.
