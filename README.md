<p align="center">
  <img src="Resources/AppIcon.png" width="120" alt="Inkwell">
</p>

<h1 align="center">Inkwell</h1>

<p align="center">
  Fix, rewrite or answer text in any Mac app by typing a short trigger like <code>?fix</code>.
</p>

<p align="center">
  <a href="https://github.com/devrahulbanjara/inkwell/releases"><img alt="Release" src="https://img.shields.io/github/v/release/devrahulbanjara/inkwell?style=flat-square"></a>
  <img alt="macOS 14+" src="https://img.shields.io/badge/macOS-14%2B-black?style=flat-square&logo=apple">
  <img alt="Swift" src="https://img.shields.io/badge/Swift-5.9%2B-F05138?style=flat-square&logo=swift&logoColor=white">
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-blue?style=flat-square"></a>
</p>

---

Inkwell sits in your menu bar and watches for triggers as you type. Write a message in Slack, Mail, Notes or a browser, end it with `?fix`, and a moment later the text in that field is replaced with the corrected version. No copy-pasting into a chat window.

```
hey can u send me the reprot by tmrw ?fix
→ Hey, can you send me the report by tomorrow?
```

## Triggers

| Trigger    | Does                                              |
|------------|---------------------------------------------------|
| `?fix`     | Fixes spelling, grammar and punctuation           |
| `?rewrite` | Rewrites the text so it reads more clearly        |
| `?formal`  | Rewrites it in a professional tone                |
| `?short`   | Makes it shorter                                  |
| `?ask`     | Answers the question or does what the text asks   |
| `?reply`   | Writes a reply under a message you received       |
| `?en`      | Translates to English                             |

You can edit any of these or add your own under **Settings → Templates**.

## Providers

Pick one of four backends. You can switch between them from the menu bar at any time.

| Provider        | What you need                                                                |
|-----------------|------------------------------------------------------------------------------|
| **Gemini**      | A free API key from [Google AI Studio](https://aistudio.google.com/apikey)   |
| **Groq**        | A free API key from [GroqCloud](https://console.groq.com/keys)               |
| **Claude Code** | The `claude` CLI, signed in to your Claude plan                              |
| **Codex**       | The `codex` CLI, signed in to your ChatGPT plan                              |

For Gemini and Groq, Inkwell only offers models on the free tier. You can add several keys and set a primary model with fallbacks. When a key or model hits a rate limit, Inkwell rests it until the limit resets and carries on with the next one. Gemini limits apply per Google Cloud project, so extra keys only help if they come from different projects.

Claude Code and Codex run headless from `~/.inkwell`, with tools disabled and no saved sessions, so they never touch your projects or clutter your history. No extra API key is needed.

## Other features

- A menu bar dashboard showing runs, tokens, words processed and AI response time over 7 days, 30 days or all time
- Providers, API keys and models are all managed from the menu bar, with full settings one click away
- A small glass indicator shows while it works and never steals focus
- Results are pasted, so <kbd>⌘Z</kbd> undoes them, and your clipboard is restored afterwards
- API keys are stored in the macOS Keychain, usage stats stay on your Mac, and text goes only to the provider you choose
- Password fields are never read
- Built with Swift and SwiftUI, has no third-party dependencies, and uses Liquid Glass on macOS 26 and later

## Install

**Homebrew**

```sh
brew tap devrahulbanjara/inkwell
brew trust devrahulbanjara/inkwell
brew install --cask inkwell
```

**DMG:** download the latest release from the [Releases](https://github.com/devrahulbanjara/inkwell/releases) page and drag Inkwell into Applications.

### First launch

Inkwell isn't notarized by Apple, so macOS blocks it the first time you open it.

1. Open Inkwell, then go to **System Settings → Privacy & Security** and click **Open Anyway**.
2. Turn Inkwell on under **Privacy & Security → Accessibility**. Without this it can't read or replace text.
3. Click the pen icon in the menu bar, open the provider card, and add a key (or sign in to Claude Code or Codex).
4. Type something in any text field, end it with `?fix`, and check the result.

## Building from source

You need Xcode or the Command Line Tools with the macOS 26 SDK or newer. The app itself runs on macOS 14 and later.

```sh
git clone https://github.com/devrahulbanjara/inkwell.git
cd inkwell
swift run                      # run straight from source
ARCHS="" ./scripts/build.sh    # build/Inkwell.app for your Mac's architecture
./scripts/make-dmg.sh          # build/Inkwell-<version>.dmg
```

`./scripts/build.sh` with no `ARCHS` builds a universal app, which needs full Xcode.

> macOS ties the Accessibility permission to the app's signature. If you rebuild and triggers stop working, toggle Inkwell off and on in Accessibility settings, or run `tccutil reset Accessibility io.github.inkwell`.

## How it works

1. A global key monitor keeps the last 64 typed characters in memory. Nothing is logged.
2. When those characters end in a trigger, Inkwell reads the focused text field through the Accessibility API. If the app doesn't expose its text that way, it falls back to <kbd>⌘A</kbd> <kbd>⌘C</kbd>.
3. It removes the trigger, sends the text with the template's instruction to your provider, and pastes the result back.

**Known limitations:** Replace mode replaces the whole field, so it works best in message boxes and short fields rather than long documents. The keyboard shortcuts Inkwell simulates assume a QWERTY layout.

## Contributing

Bug reports and pull requests are welcome. If you plan a larger change, please open an issue first to discuss it.

## Author

Made by **Rahul Dev Banjara**: [rahuldevbanjara.com.np](https://rahuldevbanjara.com.np) · [LinkedIn](https://www.linkedin.com/in/devrahulbanjara/) · [GitHub](https://github.com/devrahulbanjara)

## License

[MIT](LICENSE)
