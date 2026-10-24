# macOS app bundle

Run `packaging/macos/build-app.sh` on an Apple Silicon Mac to publish and bundle the self-contained `osx-arm64` desktop app.
The bundle is created as `MyShows Companion.app` in the project root. The script refuses to overwrite an existing bundle.

The current icon is a generated placeholder (`MyShows Companion.app/Contents/Resources/MyShowsCompanion.icns`). Replace the vector drawing in `create-placeholder-icon.swift` and rebuild when a final icon is available. The bundle is intentionally unsigned and not notarized.
