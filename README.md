# CloudGI Host

English | [简体中文](README.zh-CN.md)

This repository contains the Windows Host.

CloudGI aims to bring the convenience of the official Genshin Impact cloud experience to hardware you already own. Run the game on your own computer and play from your phone, with a pick-up-and-play experience built around one button: **Start Game**.

It's for playing from the sofa instead of sitting at a desk, or for players abroad who can't access the official cloud service. Your existing computer does the heavy lifting.

## Two apps, one experience

| App | What it does | Current focus |
| --- | --- | --- |
| [CloudGI Host](https://github.com/Ruize128/CloudGI-Host) | Runs and renders the game, then streams it to your device. | Windows |
| [CloudGI Client](https://github.com/Ruize128/CloudGI-Client) | Plays the stream and sends your controls back to the Host. | Android (tested) |

Streaming is built on [Sunshine](https://github.com/LizardByte/Sunshine) and [Moonlight](https://github.com/moonlight-stream/moonlight-android), both included in the apps. No separate installation or manual PIN pairing is needed. After the initial Host setup, just open the Client and tap **Start Game**.

## What works today

- Automatic host discovery and pairing on your local network.
- One-tap game launch and streaming, with the game closed when the session ends.
- Native multi-touch controls and Genshin Impact's mobile UI on Windows, using an open-source touchscreen UI component.

The current version streams the entire display. **Host and Client must be on the same local network.**

## Roadmap

- **Host:** Keep Windows as the main platform. Explore a background service and isolated streaming so the game can be streamed without disrupting normal foreground desktop use.
- **Client:** Broaden Android support and expand to iOS, Windows, macOS and Linux, with a browser client as a longer-term goal.
- **More games:** Games with both desktop and mobile control layouts are potential candidates, such as *Honkai: Star Rail*. Genshin Impact is the only game tested so far.
- **Beyond LAN:** Explore wide-area network streaming, with additional security measures before opening access beyond the local network.

## Development and credits

Touchscreen UI support is adapted from [FufuLauncher.UnlockerIsland](https://github.com/FufuLauncher/FufuLauncher.UnlockerIsland). Third-party source and license notices are included in each repository.

CloudGI is still under active development.
