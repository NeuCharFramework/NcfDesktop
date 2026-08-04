# NCF Mobile for iPhone

`NcfMobileApp.iOS` is a native .NET for iOS/UIKit client for a remotely hosted NCF site.
It is intentionally separate from the Avalonia desktop application so that iOS lifecycle,
audio routing, signing, and a future CarPlay scene can use Apple's native platform APIs.

## Current capabilities

- Install and run as a normal iPhone application.
- Connect only to an HTTPS NCF site (loopback HTTP is allowed for simulator development).
- Authenticate through the existing Admin API and verify `AdminOnly` access.
- Select the latest AdminChat session or create a new session.
- Load history and send messages through the streaming SSE endpoint.
- Fall back to the nonstreaming endpoint when the remote site doesn't expose SSE.
- Read the final Agent response with `AVSpeechSynthesizer`. iOS sends this audio to the
  currently selected output, including a connected car audio route when the system exposes it.
- Use the iOS keyboard's dictation button for initial speech-to-text input.

The password is never persisted. The JWT access token is held only in memory. The site URL,
user name, and automatic-read-aloud preference are stored in `NSUserDefaults`.

## Why Senparc.Web is remote-only

The existing desktop product starts `Senparc.Web` as a child process and relies on a desktop
.NET/ASP.NET runtime, dynamically discovered modules, local files, and an embedded desktop
WebView. iOS applications cannot launch a general-purpose child process, and App Store/device
builds use ahead-of-time compilation with strict trimming and code-signing constraints.

Running the full NCF host inside the iPhone application would therefore require a separate,
large AOT and trimming migration across Senparc.Web and all selected XNCF modules. That path is
not established by this prototype. The supported v0.1 architecture is:

```text
iPhone NCF Mobile -> HTTPS AdminChat API -> remotely hosted Senparc.Web
                  <- SSE streaming reply <-
```

## Prerequisites

1. A Mac with the full Xcode application installed, not only Command Line Tools.
2. Xcode's first-launch components and an iOS Simulator/runtime.
3. .NET 10 SDK and the `ios` workload.
4. An Apple Account signed in to Xcode. A free Personal Team is sufficient for installing a
   normal development build on your own iPhone, but its provisioning profile expires after
   seven days.
5. A remotely reachable NCF site with a trusted HTTPS certificate and AdminChat enabled.

After installing Xcode:

```bash
sudo xcode-select -s /Applications/Xcode.app/Contents/Developer
sudo xcodebuild -runFirstLaunch
dotnet workload install ios
```

Before signing, change `ApplicationId` in `NcfMobileApp.iOS.csproj` to a bundle identifier that
belongs to your Personal Team, for example `com.yourname.ncfmobile`.

Restore and build the device target:

```bash
dotnet restore src/NcfMobileApp.iOS/NcfMobileApp.iOS.csproj
dotnet build src/NcfMobileApp.iOS/NcfMobileApp.iOS.csproj \
  -f net10.0-ios \
  -p:RuntimeIdentifier=ios-arm64
```

Select your development team/provisioning profile through Xcode's account and device tools.
The first launch on a physical iPhone may require enabling Developer Mode and trusting the
development certificate.

## Server and network notes

- Production and physical-device connections must use HTTPS.
- A development certificate must be trusted by iOS; bypassing TLS validation is intentionally
  unsupported.
- `localhost` on a physical iPhone is the iPhone itself, not the Mac running Senparc.Web.
- Do not expose an NCF administrator endpoint directly to the public internet without normal
  firewall, authentication, rate-limit, certificate, and audit protections.

## CarPlay boundary

This project does not currently declare a CarPlay entitlement. Adding one before Apple grants
the managed capability causes signing/provisioning failure. After approval, add a CarPlay scene,
the approved entitlement, and a template-based in-car UI to this same iOS application.

The next audio milestone should replace keyboard dictation with an explicit push-to-talk service
using `AVAudioSession` (`playAndRecord` + `voiceChat`) and validate its input/output routes in a
real vehicle. Continuous wake-word recording should remain a separate privacy, power, and review
decision.
