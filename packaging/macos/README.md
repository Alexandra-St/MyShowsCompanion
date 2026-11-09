# macOS app bundle

Run `packaging/macos/build-app.sh` on an Apple Silicon Mac to publish and bundle the self-contained `osx-arm64` desktop app. The bundle is created as `MyShows Companion.app` in the project root, and the script refuses to overwrite an existing bundle.

The app icon is `packaging/macos/MyShowsCompanion.icns`, generated from the master PNG in `packaging/macos/assets/`. The bundle is ad-hoc signed; it is not Developer ID signed or notarized.
