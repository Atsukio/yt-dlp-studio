# Third-party notices

MP3 Downloader is a front end. It does not include any third-party code or binaries in this
repository or in the built `MP3Downloader.exe`. The tools below are found on your PC, or
downloaded from their official releases on first run, and are run as separate programs.

## yt-dlp

- Project: https://github.com/yt-dlp/yt-dlp
- License: [The Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE) (public domain)
- Used for: all downloading. The app calls `yt-dlp.exe` and builds its Options page from `yt-dlp --help`.
- Downloaded from: `https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe`

## FFmpeg

- Project: https://ffmpeg.org
- License: LGPL 2.1 or later / GPL, depending on the build. The build the app downloads
  (`win64-gpl-shared`) is GPL-licensed. See https://ffmpeg.org/legal.html.
- Used for: converting audio (MP3, M4A, FLAC, WAV), merging video and audio, and embedding cover art. It is called by yt-dlp.
- Downloaded from: `https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip`

FFmpeg is downloaded straight from the release above onto the user's PC and is not redistributed
by this project. If you package FFmpeg together with this app yourself, you must follow FFmpeg's
license terms, including making its source code available.

## yt-dlp/FFmpeg-Builds

- Project: https://github.com/yt-dlp/FFmpeg-Builds
- License: [MIT](https://github.com/yt-dlp/FFmpeg-Builds/blob/master/LICENSE) for the build scripts. The FFmpeg binaries it produces are under FFmpeg's license (above).
- Used for: the Windows FFmpeg build that yt-dlp recommends.

## imsyy/yt-dlp-gui

- Project: https://github.com/imsyy/yt-dlp-gui
- License: [MIT](https://github.com/imsyy/yt-dlp-gui/blob/master/LICENSE)
- Used for: design inspiration only (the Home / Downloads layout, the download cards and the
  paste-from-clipboard button). No code or assets were copied. That project is built with Tauri and
  Vue; this one is a separate C#/WPF app.

## Segoe Fluent Icons

- By Microsoft, installed with Windows 11 (falls back to Segoe MDL2 Assets on Windows 10).
- Used for: the icons in the interface, drawn from the font already installed on the user's PC.
  The font itself is not included. `src/make-icon.ps1` draws the app icon from it at build time,
  so the generated `app.ico` is not committed to this repository.
