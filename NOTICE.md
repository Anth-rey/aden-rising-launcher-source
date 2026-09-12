# What the MIT licence covers

The MIT licence in `LICENSE` covers the **source code** of the launcher.

It does not cover the Aden Rising name, logo, icons, wordmark or artwork in
`updater/Assets/` (`AdenRising.ico`, `hero.jpg`, `wordmark.png`, `icon*`,
`preview-*`, `tray.png`). Those are the project's brand and remain all rights
reserved. A fork should replace them.

## Not included

- **Fonts.** The official build embeds URW++ Nimbus Sans / Nimbus Sans
  Narrow. They are not part of this repository; without them the window falls
  back to a system font and works the same.
- **The launcher key** (`updater/LaunchKey.local.props`). It identifies the
  official launcher to the Aden Rising website when it reports a game window.
  A build without it works, but the game server treats its windows as not
  started by the official launcher.
- **The game client.** Lineage II is NCSOFT's; the launcher downloads the
  client files from the project's own storage and never ships them.
