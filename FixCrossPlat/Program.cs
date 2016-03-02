// FixCameraDate, (c) Lucian Wischik
// ------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.IO;
using System.Xml.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;


static class Program
{
    static void Main(string[] args)
    {
        var cmd = ParseCommandLine(args); if (cmd == null) return;

        foreach (var fn in cmd.Fns)
        {
            var fileToDo = GetMetadata(fn); if (fileToDo == null) continue;
            if (cmd.Pattern == "" && !cmd.Offset.HasValue) Console.WriteLine("\"{0}\": {1:yyyy.MM.dd - HH.mm.ss}", Path.GetFileName(fileToDo.fn), fileToDo.localTime);
            if (cmd.Offset.HasValue) ModifyTimestamp(cmd, fileToDo);
            if (cmd.Pattern != "") RenameFile(cmd, fileToDo);
        }
    }


    static void TestMetadata()
    {
        var fns = Directory.GetFiles("test");
        foreach (var fn in fns)
        {
            var ft = FilestampTime(fn);
            var mt = MetadataTimeAndGps(fn);
            Console.WriteLine($"{fn}\r\n    ft={ft?.Item1}\r\n    mt={mt?.Item1}\r\n    gps={mt?.Item3}");
        }
    }

    static void TestGps()
    {
        // North America
        Console.WriteLine("Montlake, Seattle - " + Gps(47.637922, -122.301557));
        Console.WriteLine("Volunteer Park, Seattle - " + Gps(47.629612, -122.315119));
        Console.WriteLine("Arboretum, Seattle - " + Gps(47.639483, -122.29801));
        Console.WriteLine("Husky Stadium, Seattle - " + Gps(47.65076, -122.302043));
        Console.WriteLine("Ballard, Seattle - " + Gps(47.668719, -122.38296));
        Console.WriteLine("Shilshole Marina, Seattle - " + Gps(47.681006, -122.407513));
        Console.WriteLine("Space Needle, Seattle - " + Gps(47.620415, -122.349463));
        Console.WriteLine("Pike Place Market, Seattle - " + Gps(47.609839, -122.342981));
        Console.WriteLine("UW Campus, Seattle - " + Gps(47.65464, -122.30843));
        Console.WriteLine("Stuart Island, WA - " + Gps(48.67998, -123.23106));
        Console.WriteLine("Lihue, Kauai - " + Gps(21.97472, -159.3656));
        Console.WriteLine("Polihale Beach, Kauai - " + Gps(22.08223, -159.76265));
        // Europe
        Console.WriteLine("Aberdeen, Scotland - " + Gps(57.14727, -2.095665));
        Console.WriteLine("The Chanonry, Old Aberdeen - " + Gps(57.169365, -2.101216));
        Console.WriteLine("Queens// College, Cambridge - " + Gps(52.20234, 0.11589));
        Console.WriteLine("Eiffel Tower, Paris - " + Gps(48.858262, 2.293763));
        Console.WriteLine("Trevi Fountain, Rome - " + Gps(41.900914, 12.483172));
        // Canada
        Console.WriteLine("Stanley Park, Vancouver - " + Gps(49.31168, -123.14786));
        Console.WriteLine("Butchart Gardens, Vancouver Island - " + Gps(48.56686, -123.46688));
        Console.WriteLine("Sidney Island, BC - " + Gps(48.65287, -123.34463));
        // Australasia
        Console.WriteLine("Darra, Brisbane - " + Gps(-27.5014, 152.97272));
        Console.WriteLine("Sidney Opera House - " + Gps(-33.85733, 151.21516));
        Console.WriteLine("Taj Mahal, India - " + Gps(27.17409, 78.04171));
        Console.WriteLine("Forbidden City, Beijing - " + Gps(39.91639, 116.39023));
        Console.WriteLine("Angkor Wat, Cambodia - " + Gps(13.41111, 103.86234));
    }


    static CommandLine ParseCommandLine(string[] args)
    {
        var cmdArgs = new LinkedList<string>(args);
        var r = new CommandLine();

        while (cmdArgs.Count > 0)
        {
            var cmd = cmdArgs.First.Value; cmdArgs.RemoveFirst();
            if (cmd == "-rename" || cmd == "/rename")
            {
                if (!string.IsNullOrEmpty(r.Pattern)) { Console.WriteLine("duplicate -rename"); return null; }
                r.Pattern = "%{datetime} - %{fn} - %{place}";
                if (cmdArgs.Count > 0 && !cmdArgs.First.Value.StartsWith("/") && !cmdArgs.First.Value.StartsWith("-")) { r.Pattern = cmdArgs.First.Value; cmdArgs.RemoveFirst(); }
            }
            else if (cmd.StartsWith("/day") || cmd.StartsWith("/hour") || cmd.StartsWith("/minute")
                || cmd.StartsWith("-day") || cmd.StartsWith("-hour") || cmd.StartsWith("-minute"))
            {
                var len = 0; Func<int, TimeSpan> mkts = (n) => default(TimeSpan);
                if (cmd.StartsWith("/day") || cmd.StartsWith("-day")) { len = 4; mkts = (n) => TimeSpan.FromDays(n); }
                if (cmd.StartsWith("/hour") || cmd.StartsWith("-hour")) { len = 5; mkts = (n) => TimeSpan.FromHours(n); }
                if (cmd.StartsWith("/minute") || cmd.StartsWith("-minute")) { len = 7; mkts = (n) => TimeSpan.FromMinutes(n); }
                var snum = cmd.Substring(len);
                if (!snum.StartsWith("+") && !snum.StartsWith("-")) { Console.WriteLine(cmd); return null; }
                var num = 0; if (!int.TryParse(snum, out num)) { Console.WriteLine(cmd); return null; }
                r.Offset = r.Offset.HasValue ? r.Offset : new TimeSpan(0);
                r.Offset = r.Offset + mkts(num);
            }
            else if (cmd == "/?" || cmd == "/help" || cmd == "-help" || cmd == "--help")
            {
                r.Fns.Clear(); break;
            }
            else if (cmd.StartsWith("-"))
            {
                // unknown option, so error out:
                Console.WriteLine(cmd); return null;
                // We'd also like to error out on unknown options that start with "/",
                // but can't, because that's a valid filename in unix.
            }
            else
            {
                if (cmd.Contains("*") || cmd.Contains("?"))
                {
                    string globPath = Path.GetDirectoryName(cmd), globMatch = Path.GetFileName(cmd);
                    if (globPath.Contains("*") || globPath.Contains("?")) { Console.WriteLine("Can't match wildcard directory names"); return null; }
                    if (globPath == "") globPath = Directory.GetCurrentDirectory();
                    var fns = Directory.GetFiles(globPath, globMatch);
                    if (fns.Length == 0) Console.WriteLine($"Not found - \"{cmd}\"");
                    r.Fns.AddRange(fns);
                }
                else
                {
                    if (File.Exists(cmd)) r.Fns.Add(cmd);
                    else Console.WriteLine($"Not found - \"{cmd}\"");
                }
            }
        }


        if (r.Fns.Count == 0)
        {
            Console.WriteLine(@"FixCameraDate ""a.jpg"" ""b.jpg"" [-rename [""pattern""]] [-day+n] [-hour+n] [-minute+n]");
            Console.WriteLine(@"  Filename can include * and ? wildcards");
            Console.WriteLine(@"  -rename: pattern defaults to ""%{datetime} - %{fn} - %{place}"" and");
            Console.WriteLine(@"           can include %{datetime,fn,date,time,year,month,day,hour,minute,second,place}");
            Console.WriteLine(@"  -day,-hour,-minute: adjust the timestamp; can be + or -");
            Console.WriteLine();
            Console.WriteLine(@"EXAMPLES:");
            Console.WriteLine(@"FixCameraDate ""a.jpg""");
            Console.WriteLine(@"FixCameraDate ""*.jpg"" -rename ""%{date} - %{time} - %{fn}.jpg""");
            Console.WriteLine(@"FixCameraDate ""\files\*D*.mov"" -hour+8 -rename");
            return null;
        }


        if (string.IsNullOrEmpty(r.Pattern)) return r;

        // Filename heuristics:
        // (1) If the user omitted an extension from the rename string, then we re-use the one that was given to us
        // (2) If the filename already matched our datetime format, then we figure out what was the base filename
        if (!r.Pattern.Contains("%{fn}")) { Console.WriteLine("Please include %{fn} in the pattern"); return null; }
        if (r.Pattern.Contains("\\") || r.Pattern.Contains("/")) { Console.WriteLine("Folders not allowed in pattern"); return null; }
        if (r.Pattern.Split(new[] { "%{fn}" }, StringSplitOptions.None).Length != 2) { Console.WriteLine("Please include %{fn} only once in the pattern"); return null; }
        //
        // 1. Extract out the extension
        var pattern = r.Pattern;
        r.PatternExt = null;
        foreach (var potentialExt in new[] { ".jpg", ".mp4", ".mov", ".jpeg" })
        {
            if (!pattern.ToLower().EndsWith(potentialExt)) continue;
            r.PatternExt = pattern.Substring(pattern.Length - potentialExt.Length);
            pattern = pattern.Substring(0, pattern.Length - potentialExt.Length);
            break;
        }
        //
        // 2. Parse the pattern-string into its constitutent parts
        var patternSplit0 = pattern.Split(new[] { '%' });
        var patternSplit = new List<string>();
        if (patternSplit0[0].Length > 0) patternSplit.Add(patternSplit0[0]);
        for (int i = 1; i < patternSplit0.Length; i++)
        {
            var s = "%" + patternSplit0[i];
            if (!s.StartsWith("%{")) { Console.WriteLine("ERROR: wrong pattern"); return null; }
            var ib = s.IndexOf("}");
            patternSplit.Add(s.Substring(0, ib + 1));
            if (ib != s.Length - 1) patternSplit.Add(s.Substring(ib + 1));
        }

        foreach (var rsplit in patternSplit)
        {
            var part = new PatternPart();
            part.pattern = rsplit;

            if (!rsplit.StartsWith("%"))
            {
                part.generator = (_1, _2, _3) => rsplit;
                part.matcher = (rr) =>
                {
                    if (rr.Length < rsplit.Length) return -1;
                    if (rr.Substring(0, rsplit.Length) == rsplit) return rsplit.Length;
                    return -1;
                };
                var prevPart = r.PatternParts.LastOrDefault();
                if (prevPart != null && prevPart.matcher == null)
                {
                    prevPart.matcher = (rr) =>
                    {
                        var i = rr.IndexOf(rsplit);
                        if (i == -1) return rr.Length;
                        return i;
                    };
                }
                r.PatternParts.AddLast(part);
                continue;
            }

            if (rsplit.StartsWith("%{fn}"))
            {
                part.generator = (fn2, dt2, pl2) => fn2;
                part.matcher = null; // must be filled in by the next part
                r.PatternParts.AddLast(part);
                continue;
            }

            if (rsplit.StartsWith("%{place}"))
            {
                part.generator = (fn2, dt2, pl2) => pl2;
                part.matcher = null; // must be filled in by the next part
                r.PatternParts.AddLast(part);
                continue;
            }

            var escapes = new[] {"%{datetime}", "yyyy.MM.dd - HH.mm.ss", @"\d\d\d\d\.\d\d\.\d\d - \d\d\.\d\d\.\d\d",
                                   "%{date}", "yyyy.MM.dd", @"\d\d\d\d\.\d\d\.\d\d",
                                   "%{time}", "HH.mm.ss", @"\d\d\.\d\d\.\d\d",
                                   "%{year}", "yyyy", @"\d\d\d\d",
                                   "%{month}", "MM", @"\d\d",
                                   "%{day}", "dd", @"\d\d",
                                   "%{hour}", "HH", @"\d\d",
                                   "%{minute}", "mm", @"\d\d",
                                   "%{second}", "ss", @"\d\d"};
            string escape = "", fmt = "", regex = "";
            for (int i = 0; i < escapes.Length; i += 3)
            {
                if (!rsplit.StartsWith(escapes[i])) continue;
                escape = escapes[i];
                fmt = escapes[i + 1];
                regex = "^" + escapes[i + 2] + "$";
                break;
            }
            if (escape == "") { Console.WriteLine("Unrecognized {0}", rsplit); return null; }
            part.generator = (fn2, dt2, pl2) => dt2.ToString(fmt);
            part.matcher = (rr) =>
            {
                if (rr.Length < fmt.Length) return -1;
                if (new Regex(regex).IsMatch(rr.Substring(0, fmt.Length))) return fmt.Length;
                return -1;
            };
            r.PatternParts.AddLast(part);
        }

        // The last part, if it was %{fn} or %{place} will match
        // up to the remainder of the original filename
        var lastPart = r.PatternParts.Last.Value;
        if (lastPart.matcher != null) lastPart.matcher = (rr) => rr.Length;

        return r;
    }


    static FileToDo GetMetadata(string fn)
    {
        var r = new FileToDo() { fn = fn };

        var mtt = MetadataTimeAndGps(fn);
        var ftt = FilestampTime(fn);
        if (mtt == null) { Console.WriteLine("Not an image/video - \"{0}\"", Path.GetFileName(fn)); return null; }

        var mt = mtt.Item1; var ft = ftt.Item1;
        if (mt.HasValue)
        {
            r.setter = mtt.Item2;
            if (mtt.Item3 != null) r.place = Gps(mtt.Item3.Latitude, mtt.Item3.Longitude); ;

            if (mt.Value.dt.Kind == DateTimeKind.Unspecified || mt.Value.dt.Kind == DateTimeKind.Local)
            {
                // If dt.kind=Unspecified (e.g. EXIF, Sony), then the time is by assumption already local from when the picture was shot
                // If dt.kind=Local (e.g. iPhone-MOV), then the time is local, and also indicates its timezone offset
                r.localTime = mt.Value.dt;
            }
            else if (mt.Value.dt.Kind == DateTimeKind.Utc)
            {
                // If dt.Kind=UTC (e.g. Android), then time is in UTC, and we don't know how to read timezone.
                r.localTime = mt.Value.dt.ToLocalTime(); // Best we can do is guess the timezone of the computer
            }
        }
        else
        {
            r.setter = ftt.Item2;
            if (ft.dt.Kind == DateTimeKind.Unspecified)
            {
                // e.g. Windows Phone when we got the date from the filename
                r.localTime = ft.dt;
            }
            else if (ft.dt.Kind == DateTimeKind.Utc)
            {
                // e.g. all other files where we got the date from the filestamp
                r.localTime = ft.dt.ToLocalTime(); // the best we can do is guess that the photo was taken in the timezone as this computer now
            }
            else
            {
                throw new Exception("Expected filetimes to be in UTC");
            }
        }

        return r;
    }


    static void ModifyTimestamp(CommandLine cmd, FileToDo fileToDo)
    {
        using (var file = new FileStream(fileToDo.fn, FileMode.Open, FileAccess.ReadWrite))
        {
            var prevTime = fileToDo.localTime;
            var r = fileToDo.setter(file, cmd.Offset.Value);
            if (r)
            {
                fileToDo.localTime += cmd.Offset.Value;
                if (cmd.Pattern == "") Console.WriteLine("\"{0}\": {1:yyyy.MM.dd - HH.mm.ss}, corrected from {2:yyyy.MM.dd - HH.mm.ss}", Path.GetFileName(fileToDo.fn), fileToDo.localTime, prevTime);
            }
        }
    }



    static void RenameFile(CommandLine cmd, FileToDo fileToDo)
    {
        // Attempt to match the existing filename against the pattern
        var basefn = Path.GetFileNameWithoutExtension(fileToDo.fn);
        var matchremainder = basefn;
        var matchParts = new LinkedList<PatternPart>(cmd.PatternParts);
        var matchExt = cmd.PatternExt ?? Path.GetExtension(fileToDo.fn);

        while (matchParts.Count > 0 && matchremainder.Length > 0)
        {
            var matchPart = matchParts.First.Value;
            var matchLength = matchPart.matcher(matchremainder);
            if (matchLength == -1) break;
            matchParts.RemoveFirst();
            if (matchPart.pattern == "%{fn}") basefn = matchremainder.Substring(0, matchLength);
            matchremainder = matchremainder.Substring(matchLength);
        }

        if (matchremainder.Length == 0 && matchParts.Count == 2 && matchParts.First.Value.pattern == " - " && matchParts.Last.Value.pattern == "%{place}")
        {
            // hack if you had pattern like "%{year} - %{fn} - %{place}" so
            // it will match a filename like "2012 - file.jpg" which lacks a place
            matchParts.Clear();
        }

        if (matchParts.Count != 0 || matchremainder.Length > 0)
        {
            // failed to do a complete match
            basefn = Path.GetFileNameWithoutExtension(fileToDo.fn);
        }

        // Figure out the new filename
        var newfn = Path.GetDirectoryName(fileToDo.fn) + "\\";
        foreach (var patternPart in cmd.PatternParts)
        {
            newfn += patternPart.generator(basefn, fileToDo.localTime, fileToDo.place);
        }
        if (cmd.PatternParts.Count > 2 && cmd.PatternParts.Last.Value.pattern == "%{place}" && cmd.PatternParts.Last.Previous.Value.pattern == " - " && String.IsNullOrEmpty(fileToDo.place))
        {
            if (newfn.EndsWith(" - ")) newfn = newfn.Substring(0, newfn.Length - 3);
        }
        newfn += matchExt;
        if (fileToDo.fn != newfn)
        {
            if (File.Exists(newfn)) { Console.WriteLine("Already exists - " + Path.GetFileName(newfn)); return; }
            Console.WriteLine(Path.GetFileName(newfn));
            File.Move(fileToDo.fn, newfn);
        }
    }


    static HttpClient http;
    static string Gps(double latitude, double longitude)
    {
        if (http == null) { http = new HttpClient(); http.DefaultRequestHeaders.Add("User-Agent", "FixCameraDate"); }

        // 1. Make the request
        var url = $"http://nominatim.openstreetmap.org/reverse?accept-language=en&format=xml&lat={latitude:0.000000}&lon={longitude:0.000000}&zoom=18";
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
        if (result != null) parts.Add(result); else if (road != null) parts.Add(road);
        if (suburb != null) parts.Add(suburb); else if (neighbourhood != null) parts.Add(neighbourhood);
        if (city != null) parts.Add(city); else if (county != null) parts.Add(county);
        if (country == "United States of America" || country == "United Kingdom") parts.Add(state); else parts.Add(country);
        int pi = 1; while (pi < parts.Count - 1)
        {
            if (parts.Take(pi).Any(s => s.Contains(parts[pi]))) parts.RemoveAt(pi);
            else pi += 1;
        }

        // 4. Sanitize
        var r = string.Join(", ", parts);
        foreach (var disallowed in new[] { '/', '\\', '?', '%', '*', '?', ':', '|', '"', '<', '>', '.', '-' })
        {
            r = r.Replace(disallowed, ' ');
        }
        r = r.Replace("  ", " ");
        return r;
    }


    static readonly Tuple<DateTimeOffset2?, UpdateTimeFunc, GpsCoordinates> EmptyResult = new Tuple<DateTimeOffset2?, UpdateTimeFunc, GpsCoordinates>(null, (s, t) => false, null);

    static Tuple<DateTimeOffset2?, UpdateTimeFunc, GpsCoordinates> MetadataTimeAndGps(string fn)
    {
        using (var file = new FileStream(fn, FileMode.Open, FileAccess.Read))
        {
            file.Seek(0, SeekOrigin.End); var fend = file.Position;
            if (fend < 8) return EmptyResult;
            file.Seek(0, SeekOrigin.Begin);
            ushort h1 = file.Read2byte(), h2 = file.Read2byte(); var h3 = file.Read4byte();
            if (h1 == 0xFFD8) return ExifTime(file, 0, fend); // jpeg header
            if (h3 == 0x66747970) return Mp4Time(file, 0, fend); // "ftyp" prefix of mp4, mov
            return null;
        }
    }


    static Tuple<DateTimeOffset2, UpdateTimeFunc> FilestampTime(string fn)
    {
        var creationTime = File.GetCreationTime(fn);
        var writeTime = File.GetLastWriteTime(fn);
        var winnerTime = creationTime;
        if (writeTime < winnerTime) winnerTime = writeTime;
        var localTime = DateTimeOffset2.Utc(winnerTime.ToUniversalTime()); // Although they're stored in UTC on disk, the APIs give us local - time
        //
        // BUG COMPATIBILITY: Filestamp times are never as good as metadata times.
        // Windows Phone doesn't store metadata, but it does use filenames of the form "WP_20131225".
        // If we find that, we'll use it.
        int year = 0, month = 0, day = 0;
        bool hasWpName = false, usedFilenameTime = false;
        var regex = new Regex(@"WP_20\d\d\d\d\d\d");
        if (regex.IsMatch(fn))
        {
            var i = fn.IndexOf("WP_20") + 3;
            if (fn.Length >= i + 8)
            {
                hasWpName = true;
                var s = fn.Substring(i, 8);
                year = int.Parse(s.Substring(0, 4));
                month = int.Parse(s.Substring(4, 2));
                day = int.Parse(s.Substring(6, 2));

                if (winnerTime.Year == year && winnerTime.Month == month && winnerTime.Day == day)
                {
                    // good, the filestamp agrees with the filename
                }
                else
                {
                    localTime = DateTimeOffset2.Unspecified(new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Unspecified));
                    usedFilenameTime = true;
                }
            }
        }

        //
        UpdateTimeFunc lambda = (file2, off) =>
        {
            if (hasWpName)
            {
                var nt = winnerTime + off;
                if (usedFilenameTime || nt.Year != year || nt.Month != month || nt.Day != day)
                {
                    Console.WriteLine("Unable to modify time of file, since time was derived from filename"); return false;
                }
            }
            File.SetCreationTime(fn, creationTime + off);
            File.SetLastWriteTime(fn, writeTime + off);
            return true;
        };

        return Tuple.Create(localTime, lambda);
    }


    static Tuple<DateTimeOffset2?, UpdateTimeFunc, GpsCoordinates> ExifTime(Stream file, long start, long fend)
    {
        DateTime? timeLastModified=null, timeOriginal=null, timeDigitized=null;
        long posLastModified = 0, posOriginal = 0, posDigitized = 0;
        string gpsNS = "", gpsEW = "";
        double? gpsLatVal = null, gpsLongVal = null;

        var pos = start + 2;
        while (true) // Iterate through the EXIF markers
        {
            if (pos + 4 > fend) break;
            file.Seek(pos, SeekOrigin.Begin);
            var marker = file.Read2byte();
            var msize = file.Read2byte();
            //Console.WriteLine("EXIF MARKER {0:X}", marker)
            if (pos + msize > fend) break;
            var mbuf_pos = pos;
            pos += 2 + msize;
            if (marker == 0xFFDA) break; // image data follows this marker; we can stop our iteration
            if (marker != 0xFFE1) continue; // we're only interested in exif markers
            if (msize < 14) continue;
            var exif1 = file.Read4byte(); if (exif1 != 0x45786966) continue; // exif marker should start with this header "Exif"
            var exif2 = file.Read2byte(); if (exif2 != 0) continue;  // and with this header
            var exif3 = file.Read4byte();
            var ExifDataIsLittleEndian = false;
            if (exif3 == 0x49492A00) ExifDataIsLittleEndian = true;
            else if (exif3 == 0x4D4D002A) ExifDataIsLittleEndian = false;
            else continue; // unrecognized byte-order
            var ipos = file.Read4byte(ExifDataIsLittleEndian);
            if (ipos + 12 >= msize) continue;  // error  in tiff header
            //
            // Format of EXIF is a chain of IFDs. Each consists of a number of tagged entries.
            // One of the tagged entries may be "SubIFDpos = &H..." which gives the address of the
            // next IFD in the chain; if this entry is absent or 0, then we're on the last IFD.
            // Another tagged entry may be "GPSInfo = &H..." which gives the address of the GPS IFD
            //
            uint subifdpos = 0;
            uint gpsifdpos = 0;
            while (true) // iterate through the IFDs
            {
                //Console.WriteLine("  IFD @{0:X}\n", ipos)
                var ibuf_pos = mbuf_pos + 10 + ipos;
                file.Seek(ibuf_pos, SeekOrigin.Begin);
                var nentries = file.Read2byte(ExifDataIsLittleEndian);
                if (10 + ipos + 2 + nentries * 12 + 4 >= msize) break;  // error in ifd header
                file.Seek(ibuf_pos + 2 + nentries * 12, SeekOrigin.Begin);
                ipos = file.Read4byte(ExifDataIsLittleEndian);
                for (var i = 0; i < nentries; i++)
                {
                    var ebuf_pos = ibuf_pos + 2 + i * 12;
                    file.Seek(ebuf_pos, SeekOrigin.Begin);
                    var tag = file.Read2byte(ExifDataIsLittleEndian);
                    var format = file.Read2byte(ExifDataIsLittleEndian);
                    var ncomps = file.Read4byte(ExifDataIsLittleEndian);
                    var data = file.Read4byte(ExifDataIsLittleEndian);
                    //Console.WriteLine("    TAG {0:X} format={1:X} ncomps={2:X} data={3:X}", tag, format, ncomps, data)
                    if (tag == 0x8769 && format == 4)
                    {
                        subifdpos = data;
                    }
                    else if (tag == 0x8825 && format == 4)
                    {
                        gpsifdpos = data;
                    }
                    else if ((tag == 1 || tag == 3) && format == 2 && ncomps == 2)
                    {
                        var s = ((char)((int)(data >> 24))).ToString();
                        if (tag == 1) gpsNS = s; else gpsEW = s;
                    }
                    else if ((tag == 2 || tag == 4) && format == 5 && ncomps == 3 && 10 + data + ncomps < msize)
                    {
                        var ddpos = mbuf_pos + 10 + data;

                        file.Seek(ddpos, SeekOrigin.Begin);
                        var degTop = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var degBot = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var minTop = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var minBot = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var secTop = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var secBot = (double)file.Read4byte(ExifDataIsLittleEndian);
                        var deg = degTop / degBot + minTop / minBot / 60.0 + secTop / secBot / 3600.0;
                        if (tag == 2) gpsLatVal = deg;
                        else if (tag == 4) gpsLongVal = deg;
                    }
                    else if ((tag == 0x132 || tag == 0x9003 || tag == 0x9004) && format == 2 && ncomps == 20 && 10 + data + ncomps < msize)
                    {
                        var ddpos = mbuf_pos + 10 + data;
                        file.Seek(ddpos, SeekOrigin.Begin);
                        var buf = new byte[19]; file.Read(buf, 0, 19);
                        var s = System.Text.Encoding.ASCII.GetString(buf);
                        DateTime dd;
                        if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dd))
                        {
                            if (tag == 0x132) { timeLastModified = dd; posLastModified = ddpos; }
                            if (tag == 0x9003) { timeOriginal = dd; posOriginal = ddpos; }
                            if (tag == 0x9004) { timeDigitized = dd; posDigitized = ddpos; }
                            //Console.WriteLine("      {0}", dd)
                        }
                    }
                } // next
                if (ipos == 0)
                {
                    ipos = subifdpos; subifdpos = 0;
                    if (ipos == 0) { ipos = gpsifdpos; gpsifdpos = 0; }
                    if (ipos == 0) break; // indicates the last IFD in this marker
                }
            } // while
        }

        var winnerTime = timeLastModified;
        if (!winnerTime.HasValue || (timeDigitized.HasValue && timeDigitized.Value < winnerTime.Value)) winnerTime = timeDigitized;
        if (!winnerTime.HasValue || (timeOriginal.HasValue && timeOriginal.Value < winnerTime.Value)) winnerTime = timeOriginal;
        //
        var winnerTimeOffset = winnerTime.HasValue ? DateTimeOffset2.Unspecified(winnerTime.Value) : (DateTimeOffset2?)null;

        UpdateTimeFunc lambda = (file2, off) =>
        {
            if (timeLastModified.HasValue && posLastModified != 0)
            {
                var buf = Encoding.ASCII.GetBytes((timeLastModified.Value + off).ToString("yyyy:MM:dd HH:mm:ss"));
                file2.Seek(posLastModified, SeekOrigin.Begin);
                file2.Write(buf, 0, buf.Length);
            }
            if (timeOriginal.HasValue && posOriginal != 0)
            {
                var buf = Encoding.ASCII.GetBytes((timeOriginal.Value + off).ToString("yyyy:MM:dd HH:mm:ss"));
                file2.Seek(posOriginal, SeekOrigin.Begin);
                file2.Write(buf, 0, buf.Length);
            }
            if (timeDigitized.HasValue && posDigitized != 0)
            {
                var buf = Encoding.ASCII.GetBytes((timeDigitized.Value + off).ToString("yyyy:MM:dd HH:mm:ss"));
                file2.Seek(posDigitized, SeekOrigin.Begin);
                file2.Write(buf, 0, buf.Length);
            }
            return true;
        };

        GpsCoordinates gps = null;
        if ((gpsNS == "N" || gpsNS == "S") && gpsLatVal.HasValue && (gpsEW == "E" || gpsEW == "W") && gpsLongVal.HasValue)
        {
            gps = new GpsCoordinates();
            gps.Latitude = gpsNS == "N" ? gpsLatVal.Value : -gpsLatVal.Value;
            gps.Longitude = gpsEW == "E" ? gpsLongVal.Value : -gpsLongVal.Value;
        }

        return Tuple.Create(winnerTimeOffset, lambda, gps);
    }


    static Tuple<DateTimeOffset2?, UpdateTimeFunc, GpsCoordinates> Mp4Time(Stream file, long start, long fend)
    {
        // The file is made up of a sequence of boxes, with a standard way to find size and FourCC "kind" of each.
        // Some box kinds contain a kind-specific blob of binary data. Other box kinds contain a sequence
        // of sub-boxes. You need to look up the specs for each kind to know whether it has a blob or sub-boxes.
        // We look for a top-level box of kind "moov", which contains sub-boxes, and then we look for its sub-box
        // of kind "mvhd", which contains a binary blob. This is where Creation/ModificationTime are stored.
        long pos = start, payloadStart = 0, payloadEnd = 0; var boxKind = "";
        //
        while (Mp4ReadNextBoxInfo(file, pos, fend, out boxKind, out payloadStart, out payloadEnd) && boxKind != "ftyp")
        {
            pos = payloadEnd;
        }
        if (boxKind != "ftyp") return EmptyResult;
        var majorBrandBuf = new byte[4];
        file.Seek(payloadStart, SeekOrigin.Begin); file.Read(majorBrandBuf, 0, 4);
        var majorBrand = Encoding.ASCII.GetString(majorBrandBuf);
        //
        pos = start;
        while (Mp4ReadNextBoxInfo(file, pos, fend, out boxKind, out payloadStart, out payloadEnd) && boxKind != "moov")  pos = payloadEnd;
        if (boxKind != "moov") return EmptyResult;
        long moovStart = payloadStart, moovEnd = payloadEnd;
        //
        pos = moovStart; fend = moovEnd;
        while (Mp4ReadNextBoxInfo(file, pos, fend, out boxKind, out payloadStart, out payloadEnd) && boxKind != "mvhd") pos = payloadEnd;
        if (boxKind != "mvhd") return EmptyResult;
        long mvhdStart = payloadStart, mvhdEnd = payloadEnd;
        //
        pos = moovStart; fend = moovEnd;
        long cdayStart = 0, cdayEnd = 0;
        long cnthStart = 0, cnthEnd = 0;
        while (Mp4ReadNextBoxInfo(file, pos, fend, out boxKind, out payloadStart, out payloadEnd) && boxKind != "udta") pos = payloadEnd;
        if (boxKind == "udta")
        {
            long udtaStart = payloadStart, udtaEnd = payloadEnd;
            //
            pos = udtaStart; fend = udtaEnd;
            while (Mp4ReadNextBoxInfo(file, pos, fend, out boxKind, out payloadStart, out payloadEnd) && boxKind != "©day") pos = payloadEnd;
            if (boxKind == "©day") { cdayStart = payloadStart; cdayEnd = payloadEnd; }
            //
            pos = udtaStart; fend = udtaEnd;
            while (Mp4ReadNextBoxInfo(file, pos, fend, out boxKind, out payloadStart, out payloadEnd) && boxKind != "CNTH") pos = payloadEnd;
            if (boxKind == "CNTH") { cnthStart = payloadStart; cnthEnd = payloadEnd; }
        }

        // The "mvhd" binary blob consists of 1byte (version, either 0 or 1), 3bytes (flags),
        // and then either 4bytes (creation), 4bytes (modification)
        // or 8bytes (creation), 8bytes (modification)
        // If version=0 then it's the former, otherwise it's the later.
        // In both cases "creation" and "modification" are big-endian number of seconds since 1st Jan 1904 UTC
        if (mvhdEnd - mvhdStart < 20) return EmptyResult;
        file.Seek(mvhdStart + 0, SeekOrigin.Begin); int version = file.ReadByte(), numBytes = version == 0 ? 4 : 8;
        file.Seek(mvhdStart + 4, SeekOrigin.Begin);
        bool creationFix1970 = false, modificationFix1970 = false;
        var creationTime = file.ReadDate(numBytes, out creationFix1970);
        var modificationTime = file.ReadDate(numBytes, out modificationFix1970);
        // COMPATIBILITY-BUG: The spec says that these times are in UTC.
        // However, my Sony Cybershot merely gives them in unspecified time (i.e. local time but without specifying the timezone)
        // Indeed its UI doesn't even let you say what the current UTC time is.
        // I also noticed that my Sony Cybershot gives MajorBrand="MSNV", which isn't used by my iPhone or Canon or WP8.
        // I'm going to guess that all "MSNV" files come from Sony, and all of them have the bug.
        Func<DateTime, DateTimeOffset2> makeMvhdTime = (dt) =>
         {
             if (majorBrand == "MSNV") return DateTimeOffset2.Unspecified(dt);
             return DateTimeOffset2.Utc(dt);
         };

        // The "©day" binary blob consists of 2byte (string-length, big-endian), 2bytes (language-code), string
        DateTimeOffset2? dayTime = null;
        var cdayStringLen = 0; var cdayString = "";
        if (cdayStart != 0 && cdayEnd - cdayStart > 4)
        {
            file.Seek(cdayStart + 0, SeekOrigin.Begin);
            cdayStringLen = file.Read2byte();
            if (cdayStart + 4 + cdayStringLen <= cdayEnd)
            {
                file.Seek(cdayStart + 4, SeekOrigin.Begin);
                var buf = new byte[cdayStringLen];
                file.Read(buf, 0, cdayStringLen);
                cdayString = Encoding.ASCII.GetString(buf);
                DateTimeOffset d; if (DateTimeOffset.TryParse(cdayString, out d)) dayTime = DateTimeOffset2.Local(d);
            }
        }

        // The "CNTH" binary blob consists of 8bytes of unknown, followed by EXIF data
        DateTimeOffset2? cnthTime = null; UpdateTimeFunc cnthLambda = null; GpsCoordinates cnthGps = null;
        if (cnthStart != 0 && cnthEnd - cnthStart > 16)
        {
            var exif_ft = ExifTime(file, cnthStart + 8, cnthEnd);
            cnthTime = exif_ft.Item1; cnthLambda = exif_ft.Item2; cnthGps = exif_ft.Item3;
        }

        DateTimeOffset2? winnerTime = null;
        if (dayTime.HasValue)
        {
            Debug.Assert(dayTime.Value.dt.Kind == DateTimeKind.Local);
            winnerTime = dayTime;
            // prefer this best of all because it knows local time and timezone
        }
        else if (cnthTime.HasValue)
        {
            Debug.Assert(cnthTime.Value.dt.Kind == DateTimeKind.Unspecified);
            winnerTime = cnthTime;
            // this is second-best because it knows local time, just not timezone
        }
        else
        {
            // Otherwise, we'll make do with a UTC time, where we don't know local-time when the pic was taken, nor timezone
            if (creationTime.HasValue && modificationTime.HasValue) winnerTime = makeMvhdTime(creationTime < modificationTime ? creationTime.Value : modificationTime.Value);
            else if (creationTime.HasValue) winnerTime = makeMvhdTime(creationTime.Value);
            else if (modificationTime.HasValue) winnerTime = makeMvhdTime(modificationTime.Value);
        }

        UpdateTimeFunc lambda = (file2, offset) =>
        {
            if (creationTime.HasValue)
            {
                var dd = creationTime.Value + offset;
                file2.Seek(mvhdStart + 4, SeekOrigin.Begin);
                file2.WriteDate(numBytes, dd, creationFix1970);
            }
            if (modificationTime.HasValue)
            {
                var dd = modificationTime.Value + offset;
                file2.Seek(mvhdStart + 4 + numBytes, SeekOrigin.Begin);
                file2.WriteDate(numBytes, dd, modificationFix1970);
            }
            if (!string.IsNullOrWhiteSpace(cdayString))
            {
                DateTimeOffset dd; if (DateTimeOffset.TryParse(cdayString, out dd))
                {
                    dd = dd + offset;
                    var str2 = dd.ToString("yyyy-MM-ddTHH:mm:sszz00");
                    var buf2 = Encoding.ASCII.GetBytes(str2);
                    if (buf2.Length == cdayStringLen)
                    {
                        file2.Seek(cdayStart + 4, SeekOrigin.Begin);
                        file2.Write(buf2, 0, buf2.Length);
                    }
                }
            }
            if (cnthLambda != null) cnthLambda(file2, offset);
            return true;
        };

        return Tuple.Create(winnerTime, lambda, cnthGps);
    }


    static bool Mp4ReadNextBoxInfo(Stream f, long pos, long fend, out string boxKind, out long payloadStart, out long payloadEnd)
    {
        boxKind = ""; payloadStart = 0; payloadEnd = 0;
        if (pos + 8 > fend) return false;
        var b = new byte[4];
        f.Seek(pos, SeekOrigin.Begin);
        f.Read(b, 0, 4); if (BitConverter.IsLittleEndian) Array.Reverse(b);
        var size = BitConverter.ToUInt32(b, 0);
        f.Read(b, 0, 4);
        var kind = $"{(char)b[0]}{(char)b[1]}{(char)b[2]}{(char)b[3]}";
        if (size != 1)
        {
            if (pos + size > fend) return false;
            boxKind = kind; payloadStart = pos + 8; payloadEnd = payloadStart + size - 8; return true;
        }
        if (size == 1 && pos + 16 <= fend)
        {
            b = new byte[8];
            f.Read(b, 0, 8); if (BitConverter.IsLittleEndian) Array.Reverse(b);
            var size2 = (long)BitConverter.ToUInt64(b, 0);
            if (pos + size2 > fend) return false;
            boxKind = kind; payloadStart = pos + 16; payloadEnd = payloadStart + size2 - 16; return true;
        }
        return false;
    }



    static void Add<T, U, V>(this LinkedList<Tuple<T, U, V>> me, T arg1, U arg2, V arg3)
    {
        me.AddLast(Tuple.Create(arg1, arg2, arg3));
    }

    readonly static DateTime TZERO_1904_UTC = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    readonly static DateTime TZERO_1970_UTC = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static ushort Read2byte(this Stream f, bool fileIsLittleEndian = false)
    {
        var b = new byte[2];
        f.Read(b, 0, 2);
        if (BitConverter.IsLittleEndian != fileIsLittleEndian) Array.Reverse(b);
        return BitConverter.ToUInt16(b, 0);
    }

    static uint Read4byte(this Stream f, bool fileIsLittleEndian = false)
    {
        var b = new byte[4];
        f.Read(b, 0, 4);
        if (BitConverter.IsLittleEndian != fileIsLittleEndian) Array.Reverse(b);
        return BitConverter.ToUInt32(b, 0);
    }

    static DateTime? ReadDate(this Stream f, int numBytes, out bool fixed1970)
    {
        fixed1970 = false;

        // COMPATIBILITY-BUG: The spec says that these are expressed in seconds since 1904.
        // But my brother's Android phone picks them in seconds since 1970.
        // I'm going to guess that all dates before 1970 should be 66 years in the future
        // Note: I'm applying this correction *before* converting to date. That's because,
        // what with leap-years and stuff, it doesn't feel safe the other way around.
        if (numBytes == 4)
        {
            var b = new byte[4];
            f.Read(b, 0, 4); if (BitConverter.IsLittleEndian) Array.Reverse(b);
            var secs = BitConverter.ToUInt32(b, 0);
            if (secs == 0) return null;
            fixed1970 = (secs < (TZERO_1970_UTC - TZERO_1904_UTC).TotalSeconds);
            return fixed1970 ? TZERO_1970_UTC.AddSeconds(secs) : TZERO_1904_UTC.AddSeconds(secs);
        }
        else if (numBytes == 8)
        {
            var b = new byte[8];
            f.Read(b, 0, 8); if (BitConverter.IsLittleEndian) Array.Reverse(b);
            var secs = BitConverter.ToUInt64(b, 0);
            if (secs == 0) return null;
            fixed1970 = (secs < (TZERO_1970_UTC - TZERO_1904_UTC).TotalSeconds);
            return fixed1970 ? TZERO_1970_UTC.AddSeconds(secs) : TZERO_1904_UTC.AddSeconds(secs);
        }
        else
        {
            throw new ArgumentException("numBytes");
        }
    }

    static void WriteDate(this Stream f, int numBytes, DateTime d, bool fix1970)
    {
        if (d.Kind != DateTimeKind.Utc) throw new ArgumentException("Can only write UTC dates");
        if (numBytes == 4)
        {
            var secs = (uint)(fix1970 ? d - TZERO_1970_UTC : d - TZERO_1904_UTC).TotalSeconds;
            var b = BitConverter.GetBytes(secs);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            f.Write(b, 0, 4);
        }
        else if (numBytes == 8)
        {
            var secs = (ulong)(fix1970 ? d - TZERO_1970_UTC : d - TZERO_1904_UTC).TotalSeconds;
            var b = BitConverter.GetBytes(secs);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            f.Write(b, 0, 8);
        }
        else
        {
            throw new ArgumentException("numBytes");
        }
    }


}


public class CommandLine
{
    public string Pattern;
    public TimeSpan? Offset;
    public List<string> Fns = new List<string>();
    //
    public LinkedList<PatternPart> PatternParts = new LinkedList<PatternPart>(); // derived from Pattern
    public string PatternExt; // derived from Pattern
}

public class PatternPart
{
    public PartGenerator generator;
    public MatchFunction matcher;
    public string pattern;
}

public delegate string PartGenerator(string fn, DateTime dt, string place);
public delegate int MatchFunction(string remainder); // -1 for no-match, otherwise is the number of characters gobbled up
public delegate bool UpdateTimeFunc(Stream stream, TimeSpan off);

class FileToDo
{
    public string fn;
    public DateTime localTime;
    public UpdateTimeFunc setter;
    public string place;
}

public class GpsCoordinates
{
    public double Latitude;
    public double Longitude;
}

struct DateTimeOffset2
{
    public DateTime dt;
    public TimeSpan offset;
    // Three modes:
    // (1) Time known to be in UTC: DateTime.Kind=UTC, offset=0
    // (2) Time known to be in some specific timezone: DateTime.Kind=Local, offset gives that timezone
    // (3) Time where nothing about timezone is known: DateTime.Kind=Unspecified, offset=0

    public static DateTimeOffset2 Utc(DateTime d)
    {
        var d2 = new DateTime(d.Ticks, DateTimeKind.Utc);
        return new DateTimeOffset2 { dt = d2, offset = default(TimeSpan) };
    }
    public static DateTimeOffset2 Unspecified(DateTime d)
    {
        var d2 = new DateTime(d.Ticks, DateTimeKind.Unspecified);
        return new DateTimeOffset2 { dt = d2, offset = default(TimeSpan) };
    }
    public static DateTimeOffset2 Local(DateTimeOffset d)
    {
        var d2 = new DateTime(d.Ticks, DateTimeKind.Local);
        return new DateTimeOffset2 { dt = d2, offset = d.Offset };
    }

    public override string ToString()
    {
        if (dt.Kind == DateTimeKind.Utc) return dt.ToString("yyyy:MM:ddTHH:mm:ssZ");
        else if (dt.Kind == DateTimeKind.Unspecified) return dt.ToString("yyyy:MM:dd HH:mm:ss");
        else if (dt.Kind == DateTimeKind.Local) return dt.ToString("yyyy:MM:dd HH:mm:ss") + offset.Hours.ToString("+00;-00") + "00";
        else throw new Exception("Invalid DateTimeKind");
    }
}
