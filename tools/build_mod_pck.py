"""Builds BalatroBridge.pck: the scenes and textures Slay the Spire 2 needs for Jimbo.

StS2 finds a character's art by resource path (res://scenes/creature_visuals/<id>.tscn,
res://images/packed/character_select/char_select_<id>.png, ...). Its mod loader mounts a mod's
<id>.pck into res:// before the mod's code runs, so this pack puts Jimbo's scenes and textures
at exactly those paths.

The textures are drawn from Balatro's own sprite sheets (extracted by build_balatro.py), so no
Balatro art lives in the repo. Each one is stored the way Godot's importer would: a .png.import
remap pointing at a lossless .ctex.

    python tools/build_mod_pck.py [--balatro build/balatro] [--out sts2-mod/BalatroBridge/bin/BalatroBridge.pck]
"""
from __future__ import annotations

import argparse
import hashlib
import io
import math
import struct
import sys
from pathlib import Path

from PIL import Image, ImageFilter, ImageOps

ROOT = Path(__file__).resolve().parent.parent
GODOT_VERSION = (4, 5, 1)  # StS2's engine; a pack may not claim a newer one
PACK_FORMAT = 3
PACK_REL_FILEBASE = 2
CHARACTER = "jimbo"

# A joker cell in Balatro's 2x sheets, and the cells used here.
CELL_W, CELL_H = 142, 190
JOKER_CELL = (0, 0)  # j_joker: Jimbo himself


# --- Godot formats ----------------------------------------------------------------------------

def ctex(image: Image.Image) -> bytes:
    """A CompressedTexture2D holding one lossless WebP image, as Godot's importer writes it."""
    image = image.convert("RGBA")
    webp = io.BytesIO()
    image.save(webp, "WEBP", lossless=True, quality=100, exact=True)
    data = webp.getvalue()
    w, h = image.size
    out = io.BytesIO()
    out.write(b"GST2")
    out.write(struct.pack("<IIIIIIII", 1, w, h, 0x0D000000, 0xFFFFFFFF, 0, 0, 0))
    out.write(struct.pack("<IHHII", 2, w, h, 0, 5))  # data format WEBP, size, no mipmaps, RGBA8
    out.write(struct.pack("<I", len(data)))
    out.write(data)
    return out.getvalue()


def import_remap(ctex_path: str) -> bytes:
    return (
        "[remap]\n\n"
        'importer="texture"\n'
        'type="CompressedTexture2D"\n'
        f'path="{ctex_path}"\n'
        "metadata={\n"
        '"vram_texture": false\n'
        "}\n"
    ).encode()


def write_pck(files: dict[str, bytes], out: Path) -> None:
    """A Godot 4.5 pack (format 3) holding `files`, keyed by res:// path."""
    entries = []
    blob = io.BytesIO()
    for path, data in files.items():
        name = path.removeprefix("res://").encode()
        offset = blob.tell()
        blob.write(data)
        blob.write(b"\0" * (-blob.tell() % 32))
        entries.append((name, offset, len(data), hashlib.md5(data).digest()))

    header_size = 4 + 4 * 4 + 4 + 8 + 8 + 16 * 4
    file_base = header_size
    directory = io.BytesIO()
    directory.write(struct.pack("<I", len(entries)))
    for name, offset, size, md5 in entries:
        padded = name + b"\0" * (-len(name) % 4)
        directory.write(struct.pack("<I", len(padded)))
        directory.write(padded)
        directory.write(struct.pack("<QQ", offset, size))
        directory.write(md5)
        directory.write(struct.pack("<I", 0))
    dir_offset = file_base + blob.tell()

    with out.open("wb") as f:
        f.write(b"GDPC")
        f.write(struct.pack("<IIII", PACK_FORMAT, *GODOT_VERSION))
        f.write(struct.pack("<IQQ", PACK_REL_FILEBASE, file_base, dir_offset))
        f.write(b"\0" * (16 * 4))
        assert f.tell() == header_size
        f.write(blob.getvalue())
        f.write(directory.getvalue())


class Pack:
    def __init__(self) -> None:
        self.files: dict[str, bytes] = {}

    def texture(self, res_path: str, image: Image.Image) -> None:
        stem = res_path.rsplit("/", 1)[-1].removesuffix(".png")
        ctex_path = f"res://bolatro/imported/{stem}.ctex"
        self.files[res_path + ".import"] = import_remap(ctex_path)
        self.files[ctex_path] = ctex(image)

    def text(self, res_path: str, content: str) -> None:
        self.files[res_path] = content.encode()


# --- Images -----------------------------------------------------------------------------------

def cell(sheet: Image.Image, col: int, row: int) -> Image.Image:
    return sheet.crop((col * CELL_W, row * CELL_H, (col + 1) * CELL_W, (row + 1) * CELL_H))


def pixel_scale(image: Image.Image, factor: float) -> Image.Image:
    w, h = image.size
    return image.resize((round(w * factor), round(h * factor)), Image.NEAREST)


def fit(image: Image.Image, size: tuple[int, int], margin: int = 0) -> Image.Image:
    """`image` scaled (pixel art kept crisp) to fit inside `size`, centred on transparency."""
    box_w, box_h = size[0] - 2 * margin, size[1] - 2 * margin
    factor = min(box_w / image.width, box_h / image.height)
    scaled = image.resize((max(1, round(image.width * factor)), max(1, round(image.height * factor))),
                          Image.NEAREST if factor >= 1 else Image.LANCZOS)
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.alpha_composite(scaled, ((size[0] - scaled.width) // 2, (size[1] - scaled.height) // 2))
    return canvas


def silhouette(image: Image.Image, radius: int) -> Image.Image:
    alpha = image.getchannel("A").filter(ImageFilter.MaxFilter(radius * 2 + 1))
    white = Image.new("RGBA", image.size, (255, 255, 255, 0))
    white.putalpha(alpha)
    return white


def locked(image: Image.Image) -> Image.Image:
    grey = ImageOps.grayscale(image.convert("RGB")).point(lambda v: v * 0.45)
    out = Image.merge("RGBA", (grey, grey, grey, image.getchannel("A")))
    return out


def swirl_wipe(width: int, height: int) -> Image.Image:
    """Greyscale wipe pattern for the character transition: Balatro's spiral, read by threshold."""
    small_w, small_h = width // 8, height // 8
    img = Image.new("L", (small_w, small_h))
    px = img.load()
    for y in range(small_h):
        for x in range(small_w):
            u = (x / small_w - 0.5) * (width / height)
            v = y / small_h - 0.5
            r = math.hypot(u, v)
            a = (math.atan2(v, u) / (2 * math.pi) + 0.5 + r * 1.6) % 1.0
            px[x, y] = int(255 * min(1.0, 0.65 * r + 0.35 * a))
    return img.resize((width, height), Image.BILINEAR).convert("RGBA")


def felt(size: tuple[int, int], light: tuple[int, int, int], dark: tuple[int, int, int],
         centre: tuple[float, float] = (0.5, 0.5), cells: tuple[int, int] = (344, 161)) -> Image.Image:
    """Balatro's swirling felt, as the table's shader draws it, in chunky pixels."""
    w, h = cells
    small = Image.new("RGB", cells)
    px = small.load()
    aspect = size[0] / size[1]
    for y in range(h):
        for x in range(w):
            u = (x / w - centre[0]) * aspect
            v = y / h - centre[1]
            r = math.hypot(u, v)
            a = math.atan2(v, u) + r * 2.7
            swirl = math.sin(a * 3.0 + r * 9.0) * 0.5 + 0.5
            t = max(0.0, min(1.0, (swirl - 0.2) / 0.6))
            t = t * t * (3 - 2 * t)
            shade = 0.5 + 0.5 * max(0.0, min(1.0, (1.25 - r) / 1.1))
            px[x, y] = tuple(int((dark[i] + (light[i] - dark[i]) * t) * shade) for i in range(3))
    return small.resize(size, Image.NEAREST).convert("RGBA")


def paste(canvas: Image.Image, image: Image.Image, centre: tuple[int, int], angle: float = 0) -> None:
    if angle:
        image = image.rotate(angle, resample=Image.NEAREST, expand=True)
    shadow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    shadow.putalpha(image.getchannel("A").point(lambda a: a * 0.45))
    x, y = centre[0] - image.width // 2, centre[1] - image.height // 2
    canvas.alpha_composite(shadow, (x + 10, y + 14))
    canvas.alpha_composite(image, (x, y))


# StS2's event layout zooms its art past the screen's edges and puts the text on the right, so
# what shows is roughly x 260-800, y 60-700 of this 1720x806 canvas: the art goes there.
def dealer_portrait(jokers: Image.Image, enhancers: Image.Image) -> Image.Image:
    """The Dealer's table: Jimbo over three of Balatro's decks, fanned on green felt."""
    canvas = felt((1720, 806), (62, 107, 90), (16, 24, 30), centre=(0.31, 0.5))
    for i, (col, row) in enumerate([(0, 2), (0, 0), (1, 2)]):  # Blue, Red and Yellow decks
        back = pixel_scale(cell(enhancers, col, row), 1.2)
        paste(canvas, back, (380 + i * 150, 560 - (1 - abs(i - 1)) * 25), angle=(1 - i) * 14)
    paste(canvas, pixel_scale(cell(jokers, *JOKER_CELL), 1.6), (530, 290), angle=-4)
    return canvas


def shop_portrait(sheets: Path, jokers: Image.Image) -> Image.Image:
    """Balatro's shop: its sign, a couple of jokers and a pack, on the shop's blue felt."""
    canvas = felt((1720, 806), (59, 93, 122), (14, 18, 28), centre=(0.31, 0.45))
    sign = Image.open(sheets / "ShopSignAnimation.png").convert("RGBA").crop((0, 0, 226, 114))
    paste(canvas, pixel_scale(sign, 2), (530, 210))
    boosters = Image.open(sheets / "boosters.png").convert("RGBA")
    paste(canvas, pixel_scale(cell(jokers, 2, 0), 1.2), (380, 520), angle=8)
    paste(canvas, pixel_scale(cell(boosters, 0, 0), 1.2), (530, 545))
    paste(canvas, pixel_scale(cell(jokers, 5, 1), 1.2), (680, 520), angle=-8)
    return canvas


def tag_portrait(sheets: Path) -> Image.Image:
    """A skipped blind's table: a scatter of Balatro's tags on dark felt."""
    canvas = felt((1720, 806), (70, 70, 110), (14, 14, 26), centre=(0.31, 0.5))
    tags = Image.open(sheets / "tags.png").convert("RGBA")
    picks = [(1, 0), (0, 0), (2, 1), (5, 0), (3, 2), (0, 3)]
    spots = [(530, 330, 3.2, 0), (360, 220, 2.0, 10), (700, 230, 2.0, -12), (330, 500, 2.2, -8), (720, 520, 2.2, 6), (530, 600, 1.8, 0)]
    for (col, row), (x, y, scale, angle) in zip(picks, spots):
        icon = tags.crop((col * 68, row * 68, (col + 1) * 68, (row + 1) * 68))
        paste(canvas, pixel_scale(icon, scale), (x, y), angle=angle)
    return canvas


def wild_card_portrait(sheets: Path, jokers: Image.Image, enhancers: Image.Image) -> Image.Image:
    """A Wild Card: Jimbo leaning over a card table in the Spire, one card face up between two face down."""
    canvas = felt((1720, 806), (120, 40, 52), (22, 10, 16), centre=(0.31, 0.5))
    deck = Image.open(sheets / "8BitDeck.png").convert("RGBA")
    ace = cell(enhancers, 1, 0).copy()  # a plain card front...
    ace.alpha_composite(cell(deck, 12, 3))  # ...with the Ace of Spades on it
    for dx, angle in ((-160, 14), (160, -14)):
        paste(canvas, pixel_scale(cell(enhancers, 0, 0), 1.1), (530 + dx, 600), angle=angle)
    paste(canvas, pixel_scale(cell(jokers, *JOKER_CELL), 1.7), (530, 265), angle=6)
    paste(canvas, pixel_scale(ace, 1.3), (530, 590))
    return canvas


# --- Scenes -----------------------------------------------------------------------------------

def creature_visuals() -> str:
    # NCreatureVisuals needs %Visuals, %Bounds, %CenterPos and %IntentPos; a plain Node2D body
    # (no Spine) is supported. The origin is at Jimbo's feet.
    return """[gd_scene load_steps=3 format=3]

[ext_resource type="Script" path="res://src/Core/Nodes/Combat/NCreatureVisuals.cs" id="1"]
[ext_resource type="Texture2D" path="res://images/bolatro/jimbo_body.png" id="2"]

[node name="Jimbo" type="Node2D"]
script = ExtResource("1")

[node name="FormVfx" type="Control" parent="."]
unique_name_in_owner = true
layout_mode = 3
anchors_preset = 0

[node name="Visuals" type="Node2D" parent="."]
unique_name_in_owner = true

[node name="Body" type="Sprite2D" parent="Visuals"]
texture_filter = 1
position = Vector2(0, -150)
texture = ExtResource("2")

[node name="Bounds" type="Control" parent="."]
unique_name_in_owner = true
layout_mode = 3
anchors_preset = 0
offset_left = -112.0
offset_top = -296.0
offset_right = 112.0
mouse_filter = 2

[node name="CenterPos" type="Marker2D" parent="."]
unique_name_in_owner = true
position = Vector2(0, -150)

[node name="IntentPos" type="Marker2D" parent="."]
unique_name_in_owner = true
position = Vector2(0, -330)

[node name="TalkPos" type="Marker2D" parent="."]
unique_name_in_owner = true
position = Vector2(60, -300)
"""


def inherit(name: str, base: str, hidden: bool = False) -> str:
    """A scene that is another scene (for rooms Jimbo never shows himself in)."""
    visibility = "visible = false\n" if hidden else ""
    return f"""[gd_scene load_steps=2 format=3]

[ext_resource type="PackedScene" path="{base}" id="1"]

[node name="{name}" instance=ExtResource("1")]
{visibility}"""


def character_icon() -> str:
    return """[gd_scene load_steps=2 format=3]

[ext_resource type="Texture2D" path="res://images/ui/top_panel/character_icon_jimbo.png" id="1"]

[node name="JimboIcon" type="TextureRect"]
anchors_preset = 15
anchor_right = 1.0
anchor_bottom = 1.0
grow_horizontal = 2
grow_vertical = 2
mouse_filter = 2
texture = ExtResource("1")
expand_mode = 1
stretch_mode = 5
"""


FELT_SHADER = """shader_type canvas_item;
uniform vec4 colour_1 : source_color = vec4(0.24, 0.42, 0.35, 1.0);
uniform vec4 colour_2 : source_color = vec4(0.07, 0.10, 0.13, 1.0);
void fragment() {
    vec2 uv = floor(UV * vec2(320.0, 150.0)) / vec2(320.0, 150.0);
    uv = (uv - vec2(0.62, 0.5)) * vec2(2.13, 1.0);
    float r = length(uv);
    float a = atan(uv.y, uv.x) + TIME * 0.06 + r * (2.5 + sin(TIME * 0.11));
    float swirl = sin(a * 3.0 + r * 9.0 - TIME * 0.35) * 0.5 + 0.5;
    vec3 col = mix(colour_2.rgb, colour_1.rgb, smoothstep(0.2, 0.8, swirl));
    col *= 0.45 + 0.55 * smoothstep(1.4, 0.1, r);
    COLOR = vec4(col, 1.0);
}
"""


def char_select_background() -> str:
    # Same frame as StS2's backgrounds (2560x1200 around the screen centre): Balatro's felt
    # swirling behind a big Jimbo.
    shader = FELT_SHADER.replace('"', '\\"')
    return f"""[gd_scene load_steps=4 format=3]

[ext_resource type="Texture2D" path="res://images/bolatro/jimbo_portrait.png" id="1"]

[sub_resource type="Shader" id="Shader_felt"]
code = "{shader}"

[sub_resource type="ShaderMaterial" id="Material_felt"]
shader = SubResource("Shader_felt")

[node name="JimboBg" type="Control"]
layout_mode = 3
anchors_preset = 8
anchor_left = 0.5
anchor_top = 0.5
anchor_right = 0.5
anchor_bottom = 0.5
offset_left = -960.0
offset_top = -523.0
offset_right = 1600.0
offset_bottom = 677.0
grow_horizontal = 2
grow_vertical = 2
pivot_offset = Vector2(1280, 600)
mouse_filter = 2

[node name="Felt" type="ColorRect" parent="."]
material = SubResource("Material_felt")
layout_mode = 0
offset_right = 2560.0
offset_bottom = 1200.0
mouse_filter = 2

[node name="Jimbo" type="TextureRect" parent="."]
texture_filter = 1
layout_mode = 0
offset_left = 1300.0
offset_top = 170.0
offset_right = 1868.0
offset_bottom = 930.0
rotation = 0.06
mouse_filter = 2
texture = ExtResource("1")
expand_mode = 1
stretch_mode = 5
"""


def transition_material() -> str:
    # The same dissolve StS2's characters use, read from Jimbo's spiral.
    return """[gd_resource type="ShaderMaterial" load_steps=3 format=3]

[ext_resource type="Texture2D" path="res://images/bolatro/jimbo_transition.png" id="1"]

[sub_resource type="Shader" id="Shader_wipe"]
code = "shader_type canvas_item;

uniform sampler2D transitionTex;
uniform float threshold : hint_range(0,1);

void fragment() {
    float falloff = 1.0 - texture(transitionTex, UV).r;
    float remap  = mix(-0.1, 1.1, threshold);
    falloff = step(falloff, remap);
    COLOR.a = falloff;
}
"

[resource]
resource_local_to_scene = true
shader = SubResource("Shader_wipe")
shader_parameter/threshold = 0.0
shader_parameter/transitionTex = ExtResource("1")
"""


# --- Build ------------------------------------------------------------------------------------

def build(balatro: Path) -> Pack:
    sheets = balatro / "resources" / "textures" / "2x"
    jokers_path = sheets / "Jokers.png"
    if not jokers_path.is_file():
        sys.exit(f"error: {jokers_path} not found (run tools/build_balatro.py first)")
    jokers = Image.open(jokers_path).convert("RGBA")
    jimbo = cell(jokers, *JOKER_CELL)

    pack = Pack()
    # Combat body: the Joker card at 1.5x its 2x art, crisp.
    pack.texture("res://images/bolatro/jimbo_body.png", pixel_scale(jimbo, 1.5))
    pack.texture("res://images/bolatro/jimbo_portrait.png", pixel_scale(jimbo, 4))
    pack.texture("res://images/bolatro/jimbo_transition.png", swirl_wipe(2560, 1200))

    select_icon = fit(jimbo, (132, 195), margin=4)
    pack.texture(f"res://images/packed/character_select/char_select_{CHARACTER}.png", select_icon)
    pack.texture(f"res://images/packed/character_select/char_select_{CHARACTER}_locked.png", locked(select_icon))

    # Top bar and dialogue icon: Jimbo's face (the top of the card art).
    face = jimbo.crop((24, 22, 118, 116))
    icon = fit(face, (85, 85), margin=3)
    pack.texture(f"res://images/ui/top_panel/character_icon_{CHARACTER}.png", icon)
    pack.texture(f"res://images/ui/top_panel/character_icon_{CHARACTER}_outline.png", silhouette(icon, 3))
    pack.texture(f"res://images/packed/map/icons/map_marker_{CHARACTER}.png", fit(jimbo, (49, 64), margin=2))

    # Jimbo's events: the Dealer at each act's start, and Balatro's shop.
    enhancers = Image.open(sheets / "Enhancers.png").convert("RGBA")
    pack.texture("res://images/events/balatro_dealer.png", dealer_portrait(jokers, enhancers))
    pack.texture("res://images/events/balatro_shop.png", shop_portrait(sheets, jokers))
    pack.texture("res://images/events/balatro_tag.png", tag_portrait(sheets))
    # Jimbo's table in other characters' runs, where Deal In comes from.
    pack.texture("res://images/events/balatro_wild_card.png", wild_card_portrait(sheets, jokers, enhancers))

    pack.text(f"res://scenes/creature_visuals/{CHARACTER}.tscn", creature_visuals())
    pack.text(f"res://scenes/ui/character_icons/{CHARACTER}_icon.tscn", character_icon())
    pack.text(f"res://scenes/screens/char_select/char_select_bg_{CHARACTER}.tscn", char_select_background())
    pack.text(f"res://materials/transitions/{CHARACTER}_transition_mat.tres", transition_material())
    # Rooms and effects Jimbo never shows himself in still need scenes to exist.
    pack.text(f"res://scenes/combat/energy_counters/{CHARACTER}_energy_counter.tscn",
              inherit("JimboEnergyCounter", "res://scenes/combat/energy_counters/ironclad_energy_counter.tscn"))
    pack.text(f"res://scenes/merchant/characters/{CHARACTER}_merchant.tscn",
              inherit("JimboMerchant", "res://scenes/merchant/characters/ironclad_merchant.tscn", hidden=True))
    pack.text(f"res://scenes/rest_site/characters/{CHARACTER}_rest_site.tscn",
              inherit("JimboRestSite", "res://scenes/rest_site/characters/ironclad_rest_site.tscn", hidden=True))
    pack.text(f"res://scenes/vfx/card_trail_{CHARACTER}.tscn",
              inherit("CardTrailJimbo", "res://scenes/vfx/card_trail_ironclad.tscn"))
    return pack


def main() -> None:
    parser = argparse.ArgumentParser(description="Build BalatroBridge.pck (Jimbo's scenes and textures).")
    parser.add_argument("--balatro", type=Path, default=ROOT / "build" / "balatro",
                        help="the bridged Balatro build, with its extracted textures (default: build/balatro)")
    parser.add_argument("--out", type=Path, default=ROOT / "sts2-mod" / "BalatroBridge" / "bin" / "BalatroBridge.pck")
    args = parser.parse_args()
    pack = build(args.balatro)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    write_pck(pack.files, args.out)
    print(f"Built {args.out} ({len(pack.files)} files, {args.out.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
