using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LifeDrop.Mobile.Visits;

// The page of the customer map in the app (step 5.52): OpenStreetMap + Leaflet like the area drawing. The user's own customers are red, the other customers of the area grey;
// the phone's position is a blue dot. Tapping a marker opens a card with the name and an "open" link (lifedrop://customer/<id>) that the page of the app catches.
// The customers go into the page as JSON, never as raw text, so a name can hold any character.
public static class CustomerMapHtml
{
    public const string OpenScheme = "lifedrop://customer/";

    public static string Build(IReadOnlyList<MapCustomerItem> customers, double? latitude, double? longitude, string openText, string mineText, string othersText)
    {
        var data = JsonSerializer.Serialize(customers.Select(c => new { id = c.Id, code = c.Code, name = c.Name, type = c.Type, lat = c.Latitude, lon = c.Longitude, mine = c.Mine }));
        var own = customers.Where(c => c.Mine).ToList();
        // Where to look first: the phone, else the middle of the user's own customers, else of all, else Erbil.
        var basis = latitude is not null && longitude is not null ? new[] { (latitude.Value, longitude.Value) }
            : (own.Count > 0 ? own : customers).Select(c => (c.Latitude, c.Longitude)).ToArray();
        var (lat, lon, zoom) = basis.Length == 0 ? (36.19, 44.01, 11) : (basis.Average(p => p.Item1), basis.Average(p => p.Item2), latitude is not null ? 14 : 12);
        return Page
            .Replace("__DATA__", data.Replace("</", "<\\/"))
            .Replace("__LAT__", lat.ToString("R", CultureInfo.InvariantCulture))
            .Replace("__LON__", lon.ToString("R", CultureInfo.InvariantCulture))
            .Replace("__ZOOM__", zoom.ToString(CultureInfo.InvariantCulture))
            .Replace("__ME__", latitude is not null && longitude is not null ? $"[{latitude.Value.ToString("R", CultureInfo.InvariantCulture)},{longitude.Value.ToString("R", CultureInfo.InvariantCulture)}]" : "null")
            .Replace("__OPEN__", JsonSerializer.Serialize(openText))
            .Replace("__MINE__", JsonSerializer.Serialize(mineText))
            .Replace("__OTHERS__", JsonSerializer.Serialize(othersText))
            .Replace("__SCHEME__", OpenScheme);
    }

    // The id of the customer a navigation address asks to open, or null for any other address.
    public static Guid? CustomerIdOf(string? url) =>
        url is not null && url.StartsWith(OpenScheme, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(url[OpenScheme.Length..].Trim('/'), out var id) ? id : null;

    private const string Page = """
        <!doctype html>
        <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
        <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
        <style>html,body,#map{height:100%;margin:0} .legend{background:#fff;padding:6px 8px;border-radius:6px;font:13px sans-serif;box-shadow:0 1px 4px #0004}
        .dot{display:inline-block;width:10px;height:10px;border-radius:50%;margin-inline-end:5px} a.open{display:inline-block;margin-top:6px;padding:4px 10px;background:#1F5143;color:#fff;border-radius:6px;text-decoration:none}</style></head>
        <body><div id="map"></div>
        <script>
        var customers = __DATA__;
        var map = L.map('map').setView([__LAT__, __LON__], __ZOOM__);
        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {maxZoom: 19, attribution: '&copy; OpenStreetMap'}).addTo(map);
        function esc(t) { var d = document.createElement('div'); d.textContent = t == null ? '' : String(t); return d.innerHTML; }
        var bounds = [];
        customers.forEach(function (c) {
          var color = c.mine ? '#dc2626' : '#6b7280';
          var m = L.circleMarker([c.lat, c.lon], {radius: c.mine ? 9 : 6, color: '#fff', weight: 2, fillColor: color, fillOpacity: 0.95}).addTo(map);
          m.bindPopup('<b>' + esc(c.name) + '</b><br>' + esc(c.code) + ' · ' + esc(c.type) + '<br><a class="open" href="__SCHEME__' + c.id + '">' + esc(__OPEN__) + '</a>');
          if (c.mine) bounds.push([c.lat, c.lon]);
        });
        var me = __ME__;
        if (me) L.circleMarker(me, {radius: 8, color: '#fff', weight: 3, fillColor: '#2563eb', fillOpacity: 1}).addTo(map);
        if (!me && bounds.length > 1) map.fitBounds(bounds, {padding: [30, 30]});
        var legend = L.control({position: 'bottomleft'});
        legend.onAdd = function () { var d = L.DomUtil.create('div', 'legend'); d.innerHTML = '<div><span class="dot" style="background:#dc2626"></span>' + esc(__MINE__) + '</div><div><span class="dot" style="background:#6b7280"></span>' + esc(__OTHERS__) + '</div>'; return d; };
        legend.addTo(map);
        </script></body></html>
        """;
}
