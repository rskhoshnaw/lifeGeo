using System.Globalization;

namespace LifeDrop.Mobile.Visits;

// The corners of the area the user draws on the map (plan step 1.27): tap to add a corner, undo, clear. The server closes the shape
// and finds the customers inside it. The rules here are the server's (3-100 corners, a real surface, not wider than half a degree),
// so a bad drawing is stopped before it is sent.
public sealed class AreaDrawing
{
    public const int MinPoints = 3;
    public const int MaxPoints = 100;
    public const double MaxDegrees = 0.5;

    private readonly List<AreaPoint> _points = [];

    public IReadOnlyList<AreaPoint> Points => _points;
    public bool IsReady => IsValid(_points);

    // Returns false for a corner that is not a real coordinate or when 100 are drawn.
    public bool Add(double latitude, double longitude)
    {
        if (_points.Count >= MaxPoints || !IsCoordinate(latitude, longitude)) return false;
        _points.Add(new AreaPoint(latitude, longitude));
        return true;
    }

    public bool Undo()
    {
        if (_points.Count == 0) return false;
        _points.RemoveAt(_points.Count - 1);
        return true;
    }

    public void Clear() => _points.Clear();

    // The map page sends "lat,lon;lat,lon;..." with dots as decimal separators whatever the phone language is.
    public static IReadOnlyList<AreaPoint>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var points = new List<AreaPoint>();
        foreach (var part in text.Trim('"', ' ').Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var numbers = part.Split(',');
            if (numbers.Length != 2 ||
                !double.TryParse(numbers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(numbers[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ||
                !IsCoordinate(lat, lon) || points.Count >= MaxPoints)
                return null;
            points.Add(new AreaPoint(lat, lon));
        }
        return points;
    }

    public static bool IsValid(IReadOnlyList<AreaPoint>? area)
    {
        if (area is null || area.Count < MinPoints || area.Count > MaxPoints || area.Any(p => !IsCoordinate(p.Latitude, p.Longitude))) return false;
        if (area.Max(p => p.Latitude) - area.Min(p => p.Latitude) > MaxDegrees || area.Max(p => p.Longitude) - area.Min(p => p.Longitude) > MaxDegrees) return false;
        double twice = 0;
        for (var i = 0; i < area.Count; i++)
        {
            var (a, b) = (area[i], area[(i + 1) % area.Count]);
            twice += a.Longitude * b.Latitude - b.Longitude * a.Latitude;
        }
        return Math.Abs(twice) > 1e-12;
    }

    private static bool IsCoordinate(double latitude, double longitude) =>
        !double.IsNaN(latitude) && !double.IsNaN(longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    // The page shown in the map view: OpenStreetMap tiles with Leaflet; a tap adds a corner. getPoints() answers "lat,lon;lat,lon".
    // Leaflet and the tiles are loaded from the internet (online only, owner decision 2026-10-06).
    public static string MapHtml(double latitude, double longitude, int zoom) => MapPage
        .Replace("__LAT__", latitude.ToString("R", CultureInfo.InvariantCulture))
        .Replace("__LON__", longitude.ToString("R", CultureInfo.InvariantCulture))
        .Replace("__ZOOM__", zoom.ToString(CultureInfo.InvariantCulture));

    private const string MapPage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
        <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
        <style>html,body,#map{height:100%;margin:0}</style></head>
        <body><div id="map"></div>
        <script>
        var map = L.map('map').setView([__LAT__, __LON__], __ZOOM__);
        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {maxZoom: 19, attribution: '&copy; OpenStreetMap'}).addTo(map);
        var points = [], markers = [], shape = null;
        function redraw() {
          if (shape) { map.removeLayer(shape); shape = null; }
          markers.forEach(function (m) { map.removeLayer(m); });
          markers = points.map(function (p) { return L.circleMarker(p, {radius: 6}).addTo(map); });
          if (points.length >= 3) shape = L.polygon(points, {color: '#2563eb'}).addTo(map);
          else if (points.length === 2) shape = L.polyline(points, {color: '#2563eb'}).addTo(map);
        }
        map.on('click', function (e) { if (points.length < 100) { points.push([e.latlng.lat, e.latlng.lng]); redraw(); } });
        function undo() { points.pop(); redraw(); }
        function clearAll() { points = []; redraw(); }
        function getPoints() { return points.map(function (p) { return p[0] + ',' + p[1]; }).join(';'); }
        </script></body></html>
        """;
}
