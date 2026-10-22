# Balatro the Spire

A Slay the Spire 2 mod that puts Balatro inside the Spire. It adds **Jimbo**, a fifth character
whose whole run is a Balatro run, and **Deal In**, a colorless card that lets any character play
a hand of Balatro.

Both run on a **bridge**. StS2 starts a hidden copy of Balatro and talks to it over a local
socket: Balatro does the real card work (dealing, poker hands, jokers, scoring) and StS2 draws it.

```
 Slay the Spire 2 (Godot 4 / .NET 9)                       Balatro (LÖVE 11 / LuaJIT)
┌──────────────────────────────────┐   localhost TCP    ┌──────────────────────────────┐
│ mods/BalatroBridge/              │   127.0.0.1:47291  │ bridged Balatro.exe          │
│   BridgeServer  (listens)        │◄──────────────────►│   bridge/core.lua (connects) │
│   BalatroSession (requests,state)│  newline-delimited │   bridge/state.lua (G → JSON)│
│   BalatroLauncher (starts game)  │       JSON         │   injected by build_balatro  │
└──────────────────────────────────┘                    └──────────────────────────────┘
```

Nothing from either game is in this repo. Jimbo's items, art and sounds are generated from your
own copy of Balatro when you build.

| Path | What |
|------|------|
| `sts2-mod/BalatroBridge/` | The StS2 mod (C#). |
| `balatro-mod/bridge/` | The Lua bridge injected into Balatro. |
| `tools/build_balatro.py` | Builds `build/balatro/Balatro.exe`: a copy of your Balatro with the bridge injected. |
| `tools/generate_balatro_content.py` | Writes Jimbo's relic and potion classes from your Balatro's item list. |
| `tools/build_mod_pck.py` | Builds `BalatroBridge.pck`: Jimbo's scenes and art, from your Balatro's sprites. |
| `tools/deploy_sts2_mod.ps1` | Runs both generators, builds the mod and installs it into StS2's `mods/` folder. |

## Requirements

- Windows, with Slay the Spire 2 and Balatro on Steam. Steam must be running to play.
- The .NET SDK, 9 or newer.
- Python 3.10 or newer, with Pillow (`pip install pillow`).

## Build and install

1. Copy Balatro's install folder (the one with `Balatro.exe`) to `games\Balatro\` in this repo.
   `games\` is git-ignored.
2. Build the bridged Balatro:

   ```powershell
   python tools\build_balatro.py        # -> build\balatro\Balatro.exe
   ```

3. Build the mod and install it into your StS2:

   ```powershell
   .\tools\deploy_sts2_mod.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
   ```

   Without `-GameDir` it installs into a copy at `games\Slay the Spire 2\`. A copy outside the
   Steam folder needs a `steam_appid.txt` next to `SlayTheSpire2.exe`. The first install also
   writes `mods\BalatroBridge\BalatroBridge.config.json`, pointing at the bridged Balatro.

4. Start StS2. The first time, it asks whether to load mods: choose yes, and start it again
   when it closes.

After either game updates, run steps 2 and 3 again.

## Playing

### Jimbo

Pick Jimbo on the character select screen. His run is a Balatro run from the first floor to the
last boss:

- **The Dealer** opens each act: pick a deck (act 1), a voucher (act 2) or a legendary joker (act 3).
- **The map** is one path of Balatro's blinds, with the coming Boss Blind showing. Hover a blind
  for its HP, reward and effect.
- **Every fight is a blind**: the enemies' HP is its chip target. Click cards to pick them (drag
  them to rearrange your hand, as in Balatro), then **Play Hand**.
  Each scoring card flies at the enemy you clicked for a share of the hand's score, then the
  suits add a little: Hearts heal, Diamonds Block, Clubs Weak, Spades Vulnerable. **Discard**
  gives Block equal to the cards' chips (in a fight with no discards, the played cards give it).
  Each turn has its share of the blind's hands and discards and ends when its hands run out.
- **Overtime**: a blind gives its hands and no more, as in Balatro. Once your turns have used
  them (4 turns at White Stake), the enemies gain Strength every turn, more each time. Jokers
  for Balatro's final hand (Dusk, Acrobat) work in Overtime.
- **A hand that dwarfs its target catches fire**, and so does Jimbo.
- **Jokers are relics** and light up in the relic bar as they score, left to right. Drag one to a
  new place, or right-click it to sell it (in the shop, drag or click it). Status cards enemies hand out land in
  his deck as dead cards. Hover any card to see what it does.
- **The deck button** in the top bar shows his deck and, as Balatro's Run Info does, every poker
  hand's level, chips × mult and times played. In the shop, it's the Run Info button.
- **After a fight**, Balatro's cash-out pays you (the blind's unused hands pay $1 each, so a quick
  win earns more) and the next node is Balatro's shop: cards, a
  voucher, booster packs, rerolls and selling. You can skip the next Small or Big Blind for its tag.
- **Consumables** (tarots, planets, spectrals) sit in the potion belt. Select cards in hand, then
  use one from the belt. Discarding one sells it.
- Ascension picks Balatro's stake. Jimbo is single-player only.

### Deal In

**Deal In** is a 1-energy colorless attack (uncommon): *"Play a hand of Balatro. Deal damage
equal to 10% of its score"* (15% upgraded, capped at 9,999). You get it from **A Wild Card**, a ?
room in the first or second act of some runs, where Jimbo deals it to you: take it, gamble for an
upgraded copy and a joker, or buy his house special. Playing it opens a Balatro table
inside StS2, where you select up to 5 cards (click, or 1-8), **Discard** (D) and **Play Hand**
(Enter). Each StS2 run has its own Balatro run: blinds carry over between plays, losing a blind
just resets it, and beating one opens Balatro's shop, where jokers you buy become relics.

## Settings and logs

`mods\BalatroBridge\BalatroBridge.config.json`:

| Key | Default | What |
|-----|---------|------|
| `balatro_exe` | `build\balatro\Balatro.exe` | The bridged Balatro to start. |
| `auto_launch` | `true` | Start Balatro together with StS2. |
| `hidden` | `true` | Run Balatro with no window or sound. Set `false` to watch it. |
| `port` | `47291` | The bridge's local port. |

StS2's log is `%APPDATA%\SlayTheSpire2\logs\godot.log` (the mod's lines start with
`[BalatroBridge]`). Balatro's side logs to `%APPDATA%\BalatroBridge\bridge.log`.

In StS2's dev console (backtick), `event BALATRO_WILD_CARD` takes you to Jimbo's table,
`card BALATRO_DEAL_IN` gives you the Deal In card and `relic BALATRO_JOKER` gives you a joker.

## Saves

The bridged Balatro uses its own profile, `%APPDATA%\BalatroBridge`, so runs started from StS2
never touch your real Balatro save. Build with `python tools\build_balatro.py --shared-saves` to
use your real profile and unlocks instead. StS2 keeps modded saves separate from unmodded ones.

Quitting StS2 closes Balatro too, and if StS2 crashes, Windows closes Balatro with it.
