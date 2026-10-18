using System.Collections.Generic;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's map: one straight path per act that walks Balatro's antes blind by blind. The act
/// opens with the Dealer (the Ancient point), then every ante is Small Blind, shop, Big Blind,
/// shop, Boss Blind, shop. Small and Big Blinds are the act's monster fights and Boss Blinds its
/// elites; the act's last Boss Blind is the act boss. The shop that follows it opens the next
/// act, after the Dealer. Balatro's 8 antes are spread over the 3 acts as 1-3, 4-6 and 7-8.
/// </summary>
internal sealed class JimboActMap : ActMap
{
    private const int Width = 7;
    private const int Column = 3;

    public override MapPoint BossMapPoint { get; }

    public override MapPoint StartingMapPoint { get; }

    protected override MapPoint?[,] Grid { get; }

    public JimboActMap(IRunState runState, int actIndex)
    {
        List<MapPointType> path = Path(actIndex);
        Grid = new MapPoint[Width, path.Count + 1];
        StartingMapPoint = new MapPoint(Column, 0) { PointType = MapPointType.Ancient };
        BossMapPoint = new MapPoint(Column, path.Count + 1) { PointType = MapPointType.Boss };

        MapPoint previous = StartingMapPoint;
        for (int i = 0; i < path.Count; i++)
        {
            var point = new MapPoint(Column, i + 1) { PointType = path[i], CanBeModified = false };
            Grid[Column, i + 1] = point;
            previous.AddChildPoint(point);
            previous = point;
        }
        previous.AddChildPoint(BossMapPoint);
        startMapPoints.Add(Grid[Column, 1]!);
    }

    /// <summary>The antes an act plays.</summary>
    public static (int First, int Last) Antes(int actIndex) => actIndex switch
    {
        0 => (1, 3),
        1 => (4, 6),
        _ => (7, 8),
    };

    /// <summary>
    /// Which blind a map row is: its ante and "Small", "Big" or "Boss" (null for shops). The act
    /// boss's row is the last ante's Boss Blind.
    /// </summary>
    public static (int Ante, string Kind)? BlindAt(int actIndex, int row)
    {
        (int first, int last) = Antes(actIndex);
        int r = actIndex > 0 ? 2 : 1; // past the shop that opens later acts
        for (int ante = first; ante <= last; ante++)
        {
            if (row == r) return (ante, "Small");
            if (row == r + 2) return (ante, "Big");
            if (row == r + 4) return (ante, "Boss");
            r += ante == last ? 4 : 6;
        }
        return row == r ? (last, "Boss") : null;
    }

    /// <summary>The points between the Dealer and the act boss.</summary>
    private static List<MapPointType> Path(int actIndex)
    {
        (int first, int last) = Antes(actIndex);
        var path = new List<MapPointType>();
        if (actIndex > 0) path.Add(MapPointType.Shop); // after the last act's boss
        for (int ante = first; ante <= last; ante++)
        {
            path.Add(MapPointType.Monster); // Small Blind
            path.Add(MapPointType.Shop);
            path.Add(MapPointType.Monster); // Big Blind
            path.Add(MapPointType.Shop);
            if (ante == last) break;         // the act boss is the last ante's Boss Blind
            path.Add(MapPointType.Elite);   // Boss Blind
            path.Add(MapPointType.Shop);
        }
        return path;
    }
}
