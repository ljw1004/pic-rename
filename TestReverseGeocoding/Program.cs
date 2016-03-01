using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Xml.Linq;


static partial class Program
{
    static void Main()
    {
        var q = new[] {
            // North America
            new WorkItem() { Tag = "Montlake, Seattle", Latitude = 47.637922, Longitude = -122.301557 },
            new WorkItem() { Tag = "Volunteer Park, Seattle", Latitude=47.629612, Longitude=-122.315119 },
            new WorkItem() { Tag = "Arboretum, Seattle", Latitude=47.639483, Longitude=-122.29801 },
            new WorkItem() { Tag = "Husky Stadium, Seattle", Latitude=47.65076, Longitude=-122.302043 },
            new WorkItem() { Tag = "Ballard, Seattle", Latitude=47.668719, Longitude=-122.38296 },
            new WorkItem() { Tag = "Shilshole Marina, Seattle", Latitude=47.681006, Longitude=-122.407513 },
            new WorkItem() { Tag = "Space Needle, Seattle", Latitude=47.620415, Longitude=-122.349463 },
            new WorkItem() { Tag = "Pike Place Market, Seattle", Latitude=47.609839, Longitude=-122.342981 },
            new WorkItem() { Tag = "UW Campus, Seattle", Latitude=47.65464, Longitude=-122.30843 },
            new WorkItem() { Tag = "Stuart Island, WA", Latitude=48.67998, Longitude=-123.23106 },
            new WorkItem() { Tag = "Lihue, Kauai", Latitude=21.97472, Longitude=-159.3656 },
            new WorkItem() { Tag = "Polihale Beach, Kauai", Latitude=22.08223, Longitude=-159.76265 },
            // Europe
            new WorkItem() { Tag = "Aberdeen, Scotland", Latitude=57.14727, Longitude=-2.095665 },
            new WorkItem() { Tag = "The Chanonry, Old Aberdeen", Latitude=57.169365, Longitude=-2.101216 },
            new WorkItem() { Tag = "Queens' College, Cambridge", Latitude=52.20234, Longitude=0.11589 },
            new WorkItem() { Tag = "Eiffel Tower, Paris", Latitude=48.858262, Longitude=2.293763 },
            new WorkItem() { Tag = "Trevi Fountain, Rome", Latitude=41.900914, Longitude=12.483172 },
            // Canada
            new WorkItem() { Tag = "Stanley Park, Vancouver", Latitude=49.31168, Longitude=-123.14786 },
            new WorkItem() { Tag = "Butchart Gardens, Vancouver Island", Latitude=48.56686, Longitude=-123.46688 },
            new WorkItem() { Tag = "Sidney Island, BC", Latitude=48.65287, Longitude=-123.34463 },
            // Australasia
            new WorkItem() { Tag = "Darra, Brisbane", Latitude=-27.5014, Longitude=152.97272 },
            new WorkItem() { Tag = "Sidney Opera House", Latitude=-33.85733, Longitude=151.21516 },
            new WorkItem() { Tag = "Taj Mahal, India", Latitude=27.17409, Longitude=78.04171 },
            new WorkItem() { Tag = "Forbidden City, Beijing", Latitude=39.91639, Longitude=116.39023 },
            new WorkItem() { Tag = "Angkor Wat, Cambodia", Latitude=13.41111, Longitude=103.86234 }
        };

        var tOSM = OpenStreetMapSearch(q);
        var tGoog = GooglePlacesSearch(q);
        var tBingLoc = BingLocationsSearch(q);
        var tBingSpat = BingSpatialSearch(q);

        foreach (var i in q)
        {
            Console.WriteLine($"{i.Tag}");
            Console.WriteLine($"   OSM:      {i.OpenStreetMapsResult}");
            Console.WriteLine($"   Goog:     {i.GooglePlacesResult}");
            Console.WriteLine($"   BingLoc:  {i.BingLocationResult}");
            Console.WriteLine($"   BingSpat: {i.BingSpatialResult}");
            Console.WriteLine();
        }
        Console.WriteLine($"TIMES");
        Console.WriteLine($"   OSM:      {tOSM.TotalMilliseconds/q.Length:0}ms/query");
        Console.WriteLine($"   Goog:     {tGoog.TotalMilliseconds/q.Length:0}ms/query");
        Console.WriteLine($"   BingLoc:  {tBingLoc.TotalMilliseconds/q.Length:0}ms/query");
        Console.WriteLine($"   BingSpat: {tBingSpat.TotalMilliseconds/q.Length:0}ms/query");
    }


    static TimeSpan OpenStreetMapSearch(IList<WorkItem> q)
    {
        // Documentation: http://wiki.openstreetmap.org/wiki/Overpass_API
        // Experiment with API: http://overpass-turbo.eu/
        // Reverse geocoding: http://wiki.openstreetmap.org/wiki/Nominatim
        // That Nominatim server is heavily rate-limited. For paid offerings, look at
        // https://developer.mapquest.com/plans
        // https://geocoder.opencagedata.com/pricing

        Console.Write("OSM           ");
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "TestReverseGeocoding");

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < q.Count; i++)
        {
            // 1. Make the web request
            Console.Write(".");
            var url = $"http://nominatim.openstreetmap.org/reverse?accept-language=en&format=xml&lat={q[i].Latitude:0.000000}&lon={q[i].Longitude:0.000000}&zoom=18";
            var raw = http.GetStringAsync(url).GetAwaiter().GetResult();
            var xml = XDocument.Parse(raw);
            // 2. Parse the response
            var result = xml.Descendants("result").FirstOrDefault()?.Attribute("ref")?.Value;
            var road = xml.Descendants("road").FirstOrDefault()?.Value;
            var neighbourhood = xml.Descendants("neighbourhood").FirstOrDefault()?.Value;
            var suburb = xml.Descendants("suburb").FirstOrDefault()?.Value;
            var city = xml.Descendants("city").FirstOrDefault()?.Value;
            var county = xml.Descendants("county").FirstOrDefault()?.Value;
            var state = xml.Descendants("state").FirstOrDefault()?.Value;
            var country = xml.Descendants("country").FirstOrDefault()?.Value;
            // 3. Assemble these into a name
            var parts = new List<string>();
            if (result != null) parts.Add(result);
            else if (road != null) parts.Add(road);
            if (suburb != null) parts.Add(suburb);
            else if (neighbourhood != null) parts.Add(neighbourhood);
            if (city != null) parts.Add(city);
            else if (county != null) parts.Add(county);
            if (country == "United States of America" || country == "United Kingdom") parts.Add(state);
            else parts.Add(country);
            int pi = 1; while (pi < parts.Count - 1)
            {
                if (parts.Take(pi).Any(s => s.Contains(parts[pi]))) parts.RemoveAt(pi);
                else pi += 1;
            }
            q[i].OpenStreetMapsResult = string.Join(", ", parts);
        }
        sw.Stop();
        Console.WriteLine();
        return sw.Elapsed;
    }


    static TimeSpan GooglePlacesSearch(IList<WorkItem> q)
    {
        // Google Place Search: https://developers.google.com/places/web-service/search

        // Google Place Search returns a load of results per location. Out of them, my heuristic for
        // picking a good single text string to describe the place:
        //   1. The name of the top point_of_interest, if there is one
        //   2. The name of the top route, if there is one, and if it's not contained in the result from [1]
        //   3. The name of the top political, if there is one, and if it's different from [1,2]
        //   4. The vicinity of the top result
        // Here below are some worked examples...
        //
        // E.g. Volunteer Park: "Volunteer Park, Volunteer Park Road, Capitol Hill, Seattle"
        //   result1: Name=Volunteer Park Road, Vicinity=Seattle, Types={route}
        //   result2: Name=Volunteer Park, Vicinity=Seattle, Types={park,point_of_interest}
        //   result3: Name=Capitol Hill, Vicinity=Seattle, Types={neighborhood,politial}
        // E.g. Eiffel Tower: "Quai Branly, Tour Eiffel - Parc du Champ-de-Mars, Paris"
        //   result1: Name=Quai Branly, Vicinity=Paris, Types={route}
        //   result2: Name=Tour Eiffel - Parc du Champ-de-Mars, Vicinity=7th arrondissement, Types={neighborhood,political}
        // E.g. Trevi Fountain: "Coin Pool, Piazza di Trevi, Rione II Trevi, Roma"
        //   result1: Name=Piazza di Trevi, Vicinity=Roma, Types={route}
        //   result2: Name=Coin Pool, Vicinity=Roma, Types={point_of_interest,establishment}
        //   result3: Name=Trevi 86, Vicinity=Piazza di Trevi, 86, Roma, Types={establishment}
        //   result4: Name=Rione II Trevi, Vicinity=Municipio I, Types={neighborhood,political}
        // E.g. Taj Mahal: "Dharmapuri, Forest Colony"
        //   result1: Name=Dharmapuri, Vicinity=Forest Colony, Types={neighbhood,political}
        //   result2: Name=Forest Colony, Vicinity=Forest Colony, Types={sublocality_level_2,sublocality,political}
        // E.g. Barking Sands Beach: "Kaiwa Road"
        //   result1: Kaiwa Road, Vicinity=<blank>, Types={route}

        if (string.IsNullOrEmpty(GoogleApiKey)) throw new Exception("Please recompile with a valid Google API Key");
        Console.Write("Google Places ");
        var http = new HttpClient();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < q.Count; i++)
        {
            // 1. Make the web request
            Console.Write(".");
            var url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/xml?key={GoogleApiKey}&location={q[i].Latitude:0.000000},{q[i].Longitude:0.000000}&radius=10";
            var raw = http.GetStringAsync(url).GetAwaiter().GetResult();
            var xml = XDocument.Parse(raw);
            var results = xml.Descendants("result");
            // 2. Parse the results
            var places = new List<Tuple<string, string, string>>(); // name, vicinity, types
            foreach (var result in results)
            {
                var name = result.Descendants("name").FirstOrDefault()?.Value;
                var vicinity = result.Descendants("vicinity").FirstOrDefault()?.Value;
                var types = string.Join(",", result.Descendants("type").Select(t => t.Value));
                places.Add(Tuple.Create(name, vicinity, types));
            }
            // 3. Pick out the top poi + route + political and make sure they're distinct
            var poi = places.FindIndex(t => t.Item3.Contains("point_of_interest"));
            var route = places.FindIndex(t => t.Item3.Contains("route"));
            var political = places.FindIndex(t => t.Item3.Contains("political"));
            var area = places.Count > 0 ? 0 : -1;
            if (route == poi) route = -1;
            if (political == poi || political == route) political = -1;
            if (poi == -1 && route == -1 && political == -1 && places.Count > 0) poi = 0;
            // 4. Assemble the parts of the place name
            var parts = new List<string>();
            if (poi != -1 && places[poi].Item1 != null) parts.Add(places[poi].Item1);
            if (route != -1 && places[route].Item1 != null && !parts.Any(p => p.Contains(places[route].Item1))) parts.Add(places[route].Item1);
            if (political != -1 && places[political].Item1 != null && !parts.Any(p => p.Contains(places[political].Item1))) parts.Add(places[political].Item1);
            if (area != -1 && places[area].Item2 != null && !parts.Any(p => p.Contains(places[area].Item2))) parts.Add(places[area].Item2);
            q[i].GooglePlacesResult = string.Join(", ", parts);
        }
        sw.Stop();
        Console.WriteLine();
        return sw.Elapsed;
    }


    static TimeSpan BingLocationsSearch(IList<WorkItem> q)
    {
        // Bing Locations API: https://msdn.microsoft.com/en-us/library/ff701710.aspx
        // This is for geocoding and reverse geocoding. It'd doesn't do
        // PointsOfInterest.
        // (There's also another more complicated form of the API that's for submitting
        // a load of requests in a single batch: https://msdn.microsoft.com/en-us/library/ff701733.aspx)

        if (string.IsNullOrEmpty(BingMapsKey)) throw new Exception("Please recompile with a valid Bing Maps Key");
        Console.Write("Bing Locations");
        var http = new HttpClient();
        var re1 = new System.Text.RegularExpressions.Regex(@"^(\d+ )");
        var re2 = new System.Text.RegularExpressions.Regex(@"( \d+)$");
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < q.Count; i++)
        {
            // 1. Make the web request. Bizzarely, Bing refuses to give both Address
            // and Neighborhood in the same query, so we have to make two queries.
            Console.Write(".");
            var url1 = $"http://dev.virtualearth.net/REST/v1/Locations/{q[i].Latitude:0.000000},{q[i].Longitude:0.000000}?includeEntityTypes=Address&includeNeighborhood=1&o=xml&key={BingMapsKey}";
            var raw1 = http.GetStringAsync(url1).GetAwaiter().GetResult();
            var xml1 = XDocument.Parse(raw1);
            var url2 = $"http://dev.virtualearth.net/REST/v1/Locations/{q[i].Latitude:0.000000},{q[i].Longitude:0.000000}?includeEntityTypes=Neighborhood,PopulatedPlace,AdminDivision1,AdminDivision2,CountryRegion&includeNeighborhood=1&o=xml&key={BingMapsKey}";
            var raw2 = http.GetStringAsync(url2).GetAwaiter().GetResult();
            var xml2 = XDocument.Parse(raw2);
            var xml = new XElement("BothResult", xml1.Root, xml2.Root);
            // 2. Parse the results. I'm just munging both results and picking the first of each descendent.
            var address = xml.Descendants(XName.Get("AddressLine", "http://schemas.microsoft.com/search/local/ws/rest/v1")).FirstOrDefault()?.Value;
            var adminDistrict = xml.Descendants(XName.Get("AdminDistrict", "http://schemas.microsoft.com/search/local/ws/rest/v1")).FirstOrDefault()?.Value;
            var adminDistrict2 = xml.Descendants(XName.Get("AdminDistrict2", "http://schemas.microsoft.com/search/local/ws/rest/v1")).FirstOrDefault()?.Value;
            var countryRegion = xml.Descendants(XName.Get("CountryRegion", "http://schemas.microsoft.com/search/local/ws/rest/v1")).FirstOrDefault()?.Value;
            var locality = xml.Descendants(XName.Get("Locality", "http://schemas.microsoft.com/search/local/ws/rest/v1")).FirstOrDefault()?.Value;
            var neighborhood = xml.Descendants(XName.Get("Neighborhood", "http://schemas.microsoft.com/search/local/ws/rest/v1")).FirstOrDefault()?.Value;
            // 3. Clean up address. "24th Ave E & Boyer" is good. "2322 26th Ave E" is too specific.
            // "Street" is useless. "Piazza di Trevi" is essentially.
            if (address != null)
            {
                int numStart = re1.Match(address).Length, numEnd = re2.Match(address).Length;
                address = address.Substring(numStart, address.Length - numStart - numEnd);
                if (address == "Street") address = null;
            }
            // 4. Reassemble the parts into a whole
            var parts = new List<string>();
            if (address != null) parts.Add(address);
            if (neighborhood != null) parts.Add(neighborhood);
            if (locality != null) parts.Add(locality);
            else if (adminDistrict2 != null) parts.Add(adminDistrict2);
            if (countryRegion == "United States" || countryRegion == "United Kingdom") parts.Add(adminDistrict);
            else parts.Add(countryRegion);
            int pi = 1; while (pi < parts.Count - 1)
            {
                if (parts.Take(pi).Any(s => s.Contains(parts[pi]))) parts.RemoveAt(pi);
                else pi += 1;
            }
            q[i].BingLocationResult = string.Join(", ", parts);
        }
        sw.Stop();
        Console.WriteLine();
        return sw.Elapsed;
    }


    static TimeSpan BingSpatialSearch(IList<WorkItem> q)
    {
        // Bing Spatial API, Query by Area: https://msdn.microsoft.com/en-us/library/gg585133.aspx
        // The Spatial API is solely for Points of Interest.
        // It only has databases for North America and Europe - the rest
        // of the world isn't available.

        if (string.IsNullOrEmpty(BingMapsKey)) throw new Exception("Please recompile with a valid Bing Maps Key");
        Console.Write("Bing Spatial  ");
        var http = new HttpClient();
        var x_Name = XName.Get("entry", "http://www.w3.org/2005/Atom");
        var x_Distance = XName.Get("__Distance", "http://schemas.microsoft.com/ado/2007/08/dataservices");
        var x_Properties = XName.Get("properties", "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata");

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < q.Count; i++)
        {
            // 1. Make the web request. Bing Spatial has one REST API for North America POIs,
            // and another for European POIs, and we don't a priori know which, so we query both.
            Console.Write(".");
            var url1 = $"http://spatial.virtualearth.net/REST/v1/data/f22876ec257b474b82fe2ffcb8393150/NavteqNA/NavteqPOIs?spatialFilter=nearby({q[i].Latitude:0.000000},{q[i].Longitude:0.000000},1.0)&$select=*,__Distance&$top=3&key={BingMapsKey}";
            var raw1 = http.GetStringAsync(url1).GetAwaiter().GetResult();
            var xml1 = XDocument.Parse(raw1);
            var url2 = $"http://spatial.virtualearth.net/REST/v1/data/c2ae584bbccc4916a0acf75d1e6947b4/NavteqEU/NavteqPOIs?spatialFilter=nearby({q[i].Latitude:0.000000},{q[i].Longitude:0.000000},1.0)&$select=*,__Distance&$top=3&key={BingMapsKey}";
            var raw2 = http.GetStringAsync(url2).GetAwaiter().GetResult();
            var xml2 = XDocument.Parse(raw2);
            var xml = new XElement("BothResult", xml1.Root, xml2.Root);
            // 2. Pick the best result, so long as it's within 150m, and parse it
            // Note that these results give no neighborhood. Also their CountryRegion is in code
            // e.g. "USA/GBR/FRA/ITA", so I'm omitting that.
            var result = xml.Descendants(x_Name)
                                .Select((e) =>
                                {
                                    var props = e.Descendants(x_Properties).FirstOrDefault();
                                    var distance = e.Descendants(x_Distance).FirstOrDefault()?.Value;
                                    var d = distance == null ? 999.0 : double.Parse(distance);
                                    return new { props = props, distance = d };
                                })
                                .Where(pd => pd.distance < 0.15)
                                .OrderBy(pd => pd.distance)
                                .Select(pd => pd.props)
                                .FirstOrDefault();
            if (result == null) { q[i].BingSpatialResult = "<unmapped>"; continue; }
            var name = result.Descendants(XName.Get("DisplayName", "http://schemas.microsoft.com/ado/2007/08/dataservices")).FirstOrDefault()?.Value;
            var locality = result.Descendants(XName.Get("Locality", "http://schemas.microsoft.com/ado/2007/08/dataservices")).FirstOrDefault()?.Value;
            var adminDistrict = result.Descendants(XName.Get("AdminDistrict", "http://schemas.microsoft.com/ado/2007/08/dataservices")).FirstOrDefault()?.Value;
            // 4. Reassemble the parts into a whole
            var parts = new List<string>();
            if (name != null) parts.Add(name);
            if (locality != null) parts.Add(locality);
            if (adminDistrict != null) parts.Add(adminDistrict);
            int pi = 1; while (pi < parts.Count - 1)
            {
                if (parts.Take(pi).Any(s => s.Contains(parts[pi]))) parts.RemoveAt(pi);
                else pi += 1;
            }
            q[i].BingSpatialResult = string.Join(", ", parts);
        }
        sw.Stop();
        Console.WriteLine();
        return sw.Elapsed;
    }
}


class WorkItem
{
    public string Tag;
    public double Latitude, Longitude;
    //
    public string OpenStreetMapsResult;
    public string GooglePlacesResult;
    public string BingLocationResult;
    public string BingSpatialResult;
}
