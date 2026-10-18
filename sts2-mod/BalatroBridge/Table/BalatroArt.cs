using System.Collections.Generic;
using BalatroBridge.Core;
using Godot;

namespace BalatroBridge.Table;

/// <summary>
/// Sprites cut from Balatro's sprite sheets (decoded by <see cref="BalatroSheets"/>). Sheet
/// textures and sprites are cached for the whole session.
/// </summary>
internal sealed class BalatroArt
{
    private static BalatroArt? _shared;

    private readonly Dictionary<string, AtlasInfo> _atlases = new();
    private readonly Dictionary<string, Texture2D?> _sheets = new();
    private readonly Dictionary<SpriteRef, Texture2D> _sprites = new();
    private int _scale = 2;

    public static BalatroArt Shared => _shared ??= new BalatroArt();

    /// <summary>Records the atlases a snapshot refers to.</summary>
    public void Learn(TableSnapshot table) => Learn(table.Atlases, table.TextureScale);

    public void Learn(IReadOnlyDictionary<string, AtlasInfo> atlases, int scale)
    {
        _scale = scale;
        foreach ((string name, AtlasInfo atlas) in atlases) _atlases[name] = atlas;
    }

    public Texture2D? Sprite(SpriteRef? sprite)
    {
        if (sprite == null || !_atlases.TryGetValue(sprite.Atlas, out AtlasInfo? atlas)) return null;
        if (_sprites.TryGetValue(sprite, out Texture2D? cached)) return cached;
        Texture2D? sheet = Sheet(atlas.Path);
        if (sheet == null) return null;
        int w = atlas.CellWidth * _scale, h = atlas.CellHeight * _scale;
        var texture = new AtlasTexture { Atlas = sheet, Region = new Rect2(sprite.X * w, sprite.Y * h, w, h), FilterClip = true };
        _sprites[sprite] = texture;
        return texture;
    }

    private Texture2D? Sheet(string relativePath)
    {
        if (_sheets.TryGetValue(relativePath, out Texture2D? sheet)) return sheet;
        Image? image = BalatroSheets.Get(relativePath);
        sheet = image != null ? ImageTexture.CreateFromImage(image) : null;
        _sheets[relativePath] = sheet;
        return sheet;
    }
}
