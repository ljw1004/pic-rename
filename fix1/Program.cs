using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Threading;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        //If BingMapsKey.BingMapsKey = "" Then Console.WriteLine("THIS VERSION HAS BEEN BUILT WITHOUT GPS SUPPORT")
        string cmdFn = "", cmdPattern = "", cmdError = ""; TimeSpan? cmdOffset = null;
        var cmdArgs = new LinkedList<string>(args);
        // Get the filename
        if (cmdArgs.Count > 0 && !cmdArgs.First.Value.StartsWith("/")) { cmdFn = cmdArgs.First.Value; cmdArgs.RemoveFirst(); }
        // Search for further switches
        while (cmdError == "" && cmdArgs.Count > 0)
        {
            var cmdSwitch = cmdArgs.First.Value; cmdArgs.RemoveFirst();
            if (cmdSwitch == "/rename")
            {
                if (cmdPattern != "") { cmdError = "duplicate /rename"; break; }
                cmdPattern = "%{datetime} - %{fn} - %{place}";
                if (cmdArgs.Count > 0 && !cmdArgs.First.Value.StartsWith("/")) { cmdPattern = cmdArgs.First.Value; cmdArgs.RemoveFirst(); }
            }
            else if (cmdSwitch.StartsWith("/day") || cmdSwitch.StartsWith("/hour") || cmdSwitch.StartsWith("/minute"))
            {
                var len = 0; Func<int, TimeSpan> mkts = (n) => default(TimeSpan);
                if (cmdSwitch.StartsWith("/day")) { len = 4; mkts = (n) => TimeSpan.FromDays(n); }
                if (cmdSwitch.StartsWith("/hour")) { len = 5; mkts = (n) => TimeSpan.FromHours(n); }
                if (cmdSwitch.StartsWith("/minute")) { len = 7; mkts = (n) => TimeSpan.FromMinutes(n); }
                var snum = cmdSwitch.Substring(len);
                if (!snum.StartsWith("+") && !snum.StartsWith("-")) { cmdError = cmdSwitch; break; }
                var num = 0; if (!int.TryParse(snum, out num)) { cmdError = cmdSwitch; break; }
                cmdOffset = cmdOffset.HasValue ? cmdOffset : new TimeSpan(0);
                cmdOffset = cmdOffset + mkts(num);
            }
            else if (cmdSwitch == "/?")
            {
                cmdFn = "";
            }
            else
            {
                cmdError = cmdSwitch; break;
            }
        }
        if (cmdError != "") { Console.WriteLine("Unrecognized command: {0}", cmdError); return; }
        if (cmdArgs.Count > 0) { throw new Exception("Failed to parse command line"); }
        if (cmdFn == "")
        {
            Console.WriteLine("FixCameraDate \"a.jpg\" [/rename [\"pattern\"]] [/day+n] [/hour+n] [/minute+n]");
            Console.WriteLine("  Filename can include * and ? wildcards");
            Console.WriteLine("  /rename: pattern defaults to \"%{datetime} - %{fn} - %{place}\" and");
            Console.WriteLine("           can include %{date/time/year/month/day/hour/minute/second/place}");
            Console.WriteLine("  /day,/hour,/minute: adjust the timestamp; can be + or -");
            Console.WriteLine();
            Console.WriteLine("EXAMPLES:");
            Console.WriteLine("FixCameraDate \"a.jpg\"");
            Console.WriteLine("FixCameraDate \"*.jpg\" /rename \"%{date} - %{time} - %{fn}.jpg\"");
            Console.WriteLine("FixCameraDate \"*D*.mov\" /hour+8 /rename");
            return;
        }

        string globPath = "", globMatch = cmdFn;
        if (globMatch.Contains("\\"))
        {
            globPath = Path.GetDirectoryName(globMatch); globMatch = Path.GetFileName(globMatch);
        }
        else
        {
            globPath = Environment.CurrentDirectory;
        }
        var globFiles = Directory.GetFiles(globPath, globMatch);
        if (globFiles.Length == 0) Console.WriteLine("Not found - \"{0}\"", cmdFn);

        var filesToDo = new Queue<FileToDo>();
        var gpsToDo = new Dictionary<int, FileToDo>();
        var gpsNextRequestId = 1;
        foreach (var globFile in globFiles) filesToDo.Enqueue(new FileToDo { fn = globFile });

    }


    delegate string PartGenerator(string fn, DateTime dt, string place);
    delegate int MatchFunction(string remainder); // -1 for no-match, otherwise is the number of characters gobbled up
    delegate bool UpdateTimeFunc(Stream stream, TimeSpan off);

    readonly Tuple<DateTimeKind?, UpdateTimeFunc, GpsCoordinates> EmptyResult = new Tuple<DateTimeKind?, UpdateTimeFunc, GpsCoordinates>(null, () => false, null);

    class GpsCoordinates
    {
        public double Latitude;
        public double Longitude;
    }

    class PatternPart
    {
        public PartGenerator generator;
        public MatchFunction matcher;
        public string pattern;
    }

    class FileToDo
    {
        // (1) Upon first creation, FileToDo merely has "fn"
        // (2) After initial scan, it also has "localTime, setter, gpsCoordinates"
        // This information might be enough for the program to complete its work on this file.
        // (3) If not, then the file gets stored in a "to-gps" queue.
        // After gps results are back, then gpsResult is populated
        public string fn;

        public bool hasInitialScan;
        public DateTime localTime;
        public UpdateTimeFunc setter;
        public GpsCoordinates gpsCoordinates;

        public string hasGpsResult;
    }

}

/*

Module Module1

    Sub Test()
        For Each fn In
{"eg-android - 2013.12.28 - 15.48 PST.jpg", "eg-android - 2013.12.28 - 15.48 PST.mp4",
                        "eg-canon-ixus - 2013.12.15 - 07.30 PST.jpg", "eg-canon-ixus - 2013.12.15 - 07.30 PST.mov",
                        "eg-canon-powershot - 2013.12.28 - 15.51 PST.jpg", "eg-canon-powershot - 2013.12.28 - 15.51 PST.mov",
                        "eg-iphone4s - 2013.12.28 - 15.49 PST.jpg", "eg-iphone4s - 2013.12.28 - 15.49 PST.mov",
                        "eg-iphone5 - 2013.12.10 - 15.40 PST.jpg", "eg-iphone5 - 2013.12.09 - 15.21 PST.mov",
                        "eg-sony-cybershot - 2013.12.15 - 07.30 PST.jpg", "eg-sony-cybershot - 2013.12.15 - 07.30 PST.mp4",
                        "eg-wp8 - 2013.12.15 - 07.33 PST.jpg", "eg-wp8 - 2013.12.15 - 07.33 PST.mp4",
                        "eg-screenshot.png", "eg-notapic.txt"}
Dim ft = FilestampTime($"test\{fn}")?.Item1
            Dim mt = MetadataTimeAndGps($"test\{fn}")?.Item1
            Console.WriteLine($"{fn}{vbCrLf}    ft={ft}{vbCrLf}    mt={mt}")
        Next
    End Sub

    Sub Main(args As String())


        While filesToDo.Count > 0 OrElse gpsToDo.Count > 0
            If filesToDo.Count = 0 Then DoGps(gpsToDo, filesToDo)
            Dim fileToDo = filesToDo.Dequeue()

            If Not fileToDo.hasInitialScan Then
                fileToDo.hasInitialScan = True
                Dim mtt = MetadataTimeAndGps(fileToDo.fn)
                Dim ftt = FilestampTime(fileToDo.fn)
                If mtt Is Nothing Then Console.WriteLine("Not an image/video - ""{0}""", IO.Path.GetFileName(fileToDo.fn)) : Continue While
                Dim mt = mtt.Item1, ft = ftt.Item1
                If mt.HasValue Then
                    fileToDo.setter = mtt.Item2
                    fileToDo.gpsCoordinates = mtt.Item3
                    If mt.Value.dt.Kind = System.DateTimeKind.Unspecified OrElse mt.Value.dt.Kind = System.DateTimeKind.Local Then
                        ' If dt.kind=Unspecified (e.g. EXIF, Sony), then the time is by assumption already local from when the picture was shot
                        ' If dt.kind=Local (e.g. iPhone-MOV), then the time is local, and also indicates its timezone offset
                        fileToDo.localTime = mt.Value.dt
                    ElseIf mt.Value.dt.Kind = System.DateTimeKind.Utc Then
                        ' If dt.Kind=UTC (e.g. Android), then time is in UTC, and we don't know how to read timezone.
                        fileToDo.localTime = mt.Value.dt.ToLocalTime() ' Best we can do is guess the timezone of the computer
                    End If
                Else
                    fileToDo.setter = ftt.Item2
                    If ft.dt.Kind = System.DateTimeKind.Unspecified Then
                        ' e.g. Windows Phone when we got the date from the filename
                        fileToDo.localTime = ft.dt
                    ElseIf ft.dt.Kind = System.DateTimeKind.Utc Then
                        ' e.g. all other files where we got the date from the filestamp
                        fileToDo.localTime = ft.dt.ToLocalTime() ' the best we can do is guess that the photo was taken in the timezone as this computer now
                    Else
                        Throw New Exception("Expected filetimes to be in UTC")
                    End If
                End If
            End If

            ' The only thing that requires GPS is if (1) we're doing a rename, (2) the
            ' pattern includes place, (3) the file actually has a GPS signature
            If cmdPattern.Contains("%{place}") AndAlso fileToDo.gpsCoordinates IsNot Nothing AndAlso fileToDo.hasGpsResult Is Nothing AndAlso Not String.IsNullOrEmpty(BingMapsKey.BingMapsKey) Then
                gpsNextRequestId += 1
                gpsToDo.Add(gpsNextRequestId, fileToDo)
                If gpsToDo.Count >= 50 Then DoGps(gpsToDo, filesToDo)
                Continue While
            End If

            ' Otherwise, by assumption here, either we have GPS result or we don't need it

            If cmdPattern = "" AndAlso Not cmdOffset.HasValue Then
                Console.WriteLine("""{0}"": {1:yyyy.MM.dd - HH.mm.ss}", IO.Path.GetFileName(fileToDo.fn), fileToDo.localTime)
            End If


            If cmdOffset.HasValue Then
                Using file = New IO.FileStream(fileToDo.fn, IO.FileMode.Open, IO.FileAccess.ReadWrite)
                    Dim prevTime = fileToDo.localTime
                    Dim r = fileToDo.setter(file, cmdOffset.Value)
                    If r Then
                        fileToDo.localTime += cmdOffset.Value
                        If cmdPattern = "" Then Console.WriteLine("""{0}"": {1:yyyy.MM.dd - HH.mm.ss}, corrected from {2:yyyy.MM.dd - HH.mm.ss}", IO.Path.GetFileName(fileToDo.fn), fileToDo.localTime, prevTime)
                    End If
                End Using
            End If


            If cmdPattern <> "" Then
                ' Filename heuristics:
                ' (1) If the user omitted an extension from the rename string, then we re-use the one that was given to us
                ' (2) If the filename already matched our datetime format, then we figure out what was the base filename
                If Not cmdPattern.Contains("%{fn}") Then Console.WriteLine("Please include %{fn} in the pattern") : Return
                If cmdPattern.Contains("\") Then Console.WriteLine("Folders not allowed in pattern") : Return
                If cmdPattern.Split({"%{fn}"}, StringSplitOptions.None).Length<> 2 Then Console.WriteLine("Please include %{fn} only once in the pattern") : Return
                '
                ' 1. Extract out the extension
                Dim pattern = cmdPattern
                Dim patternExt As String = Nothing
                For Each potentialExt In {".jpg", ".mp4", ".mov", ".jpeg"}
If Not pattern.ToLower.EndsWith(potentialExt) Then Continue For
patternExt = pattern.Substring(pattern.Length - potentialExt.Length)
                    pattern = pattern.Substring(0, pattern.Length - potentialExt.Length)
                    Exit For
                Next
                If patternExt Is Nothing Then patternExt = IO.Path.GetExtension(fileToDo.fn)
                '
                ' 2. Parse the pattern-string into its constitutent parts
                Dim patternSplit0 = pattern.Split({"%"c})
                Dim patternSplit As New List(Of String)
                If patternSplit0(0).Length > 0 Then patternSplit.Add(patternSplit0(0))
                For i = 1 To patternSplit0.Length - 1
                    Dim s = "%" & patternSplit0(i)
                    If Not s.StartsWith("%{") Then Console.WriteLine("ERROR: wrong pattern") : Return
                    Dim ib = s.IndexOf("}")
                    patternSplit.Add(s.Substring(0, ib + 1))
                    If ib<> s.Length - 1 Then patternSplit.Add(s.Substring(ib + 1))
                Next
                Dim patternParts As New LinkedList(Of PatternPart)

                For Each rsplit In patternSplit
                    Dim part As New PatternPart
                    part.pattern = rsplit

                    If Not rsplit.StartsWith("%") Then
                        part.generator = Function() rsplit
                        part.matcher = Function(rr)
                                           If rr.Length<rsplit.Length Then Return -1
                                           If rr.Substring(0, rsplit.Length) = rsplit Then Return rsplit.Length
                                           Return -1
                                       End Function
                        Dim prevPart = patternParts.LastOrDefault
                        If prevPart IsNot Nothing AndAlso prevPart.matcher Is Nothing Then
                            prevPart.matcher = Function(rr)
                                                   Dim i = rr.IndexOf(rsplit)
                                                   If i = -1 Then Return rr.Length
                                                   Return i
                                               End Function
                        End If
                        patternParts.AddLast(part)
                        Continue For
                    End If

                    If rsplit.StartsWith("%{fn}") Then
                        part.generator = Function(fn2, dt2, pl2) fn2
                        part.matcher = Nothing ' must be filled in by the next part
                        patternParts.AddLast(part)
                        Continue For
                    End If

                    If rsplit.StartsWith("%{place}") Then
                        part.generator = Function(fn2, dt2, pl2) pl2
                        part.matcher = Nothing ' must be filled in by the next part
                        patternParts.AddLast(part)
                        Continue For
                    End If

                    Dim escapes = {"%{datetime}", "yyyy.MM.dd - HH.mm.ss", "####.##.## - ##.##.##",
                                   "%{date}", "yyyy.MM.dd", "####.##.##",
                                   "%{time}", "HH.mm.ss", "##.##.##",
                                   "%{year}", "yyyy", "####",
                                   "%{month}", "MM", "##",
                                   "%{day}", "dd", "##",
                                   "%{hour}", "HH", "##",
                                   "%{minute}", "mm", "##",
                                   "%{second}", "ss", "##"}
                    Dim escape = "", fmt = "", islike = ""
                    For i = 0 To escapes.Length - 1 Step 3
                        If Not rsplit.StartsWith(escapes(i)) Then Continue For
                        escape = escapes(i)
                        fmt = escapes(i + 1)
                        islike = escapes(i + 2)
                        Exit For
                    Next
                    If escape = "" Then Console.WriteLine("Unrecognized {0}", rsplit) : Return
                    part.generator = Function(fn2, dt2, pl2) dt2.ToString(fmt)
                    part.matcher = Function(rr)
                                       If rr.Length<islike.Length Then Return -1
                                       If rr.Substring(0, islike.Length) Like islike Then Return islike.Length
                                       Return -1
                                   End Function
                    patternParts.AddLast(part)
                Next

                ' The last part, if it was %{fn} or %{place} will match
                ' up to the remainder of the original filename
                Dim lastPart = patternParts.Last.Value
                If lastPart.matcher Is Nothing Then lastPart.matcher = Function(rr) rr.Length

                '
                ' 3. Attempt to match the existing filename against the pattern
                Dim basefn = IO.Path.GetFileNameWithoutExtension(fileToDo.fn)
                Dim matchremainder = basefn
                Dim matchParts As New LinkedList(Of PatternPart)(patternParts)
                While matchParts.Count > 0 AndAlso matchremainder.Length > 0
                    Dim matchPart As PatternPart = matchParts.First.Value
                    Dim matchLength = matchPart.matcher(matchremainder)
                    If matchLength = -1 Then Exit While
                    matchParts.RemoveFirst()
                    If matchPart.pattern = "%{fn}" Then basefn = matchremainder.Substring(0, matchLength)
                    matchremainder = matchremainder.Substring(matchLength)
                End While

                If matchremainder.Length = 0 AndAlso matchParts.Count = 2 AndAlso matchParts(0).pattern = " - " AndAlso matchParts(1).pattern = "%{place}" Then
                    ' hack if you had pattern like "%{year} - %{fn} - %{place}" so
                    ' it will match a filename like "2012 - file.jpg" which lacks a place
                    matchParts.Clear()
                End If

                If matchParts.Count<> 0 OrElse matchremainder.Length > 0 Then
                    ' failed to do a complete match
                    basefn = IO.Path.GetFileNameWithoutExtension(fileToDo.fn)
                End If
                '
                ' 4. Figure out the new filename
                Dim newfn = IO.Path.GetDirectoryName(fileToDo.fn) & "\"
                For Each patternPart In patternParts
                    newfn &= patternPart.generator(basefn, fileToDo.localTime, fileToDo.hasGpsResult)
                Next
                If patternParts.Count > 2 AndAlso patternParts.Last.Value.pattern = "%{place}" AndAlso patternParts.Last.Previous.Value.pattern = " - " AndAlso String.IsNullOrEmpty(fileToDo.hasGpsResult) Then
                    If newfn.EndsWith(" - ") Then newfn = newfn.Substring(0, newfn.Length - 3)
                End If
                newfn &= patternExt
                If fileToDo.fn<> newfn Then
                   If IO.File.Exists(newfn) Then Console.WriteLine("Already exists - " & IO.Path.GetFileName(newfn)) : Continue While
                    Console.WriteLine(IO.Path.GetFileName(newfn))
                    IO.File.Move(fileToDo.fn, newfn)
                End If
            End If

        End While
    End Sub



    Sub DoGps(gpsToDo0 As Dictionary(Of Integer, FileToDo), filesToDo As Queue(Of FileToDo))
        Static Dim http As New HttpClient
        Dim gpsToDo As New Dictionary(Of Integer, FileToDo)(gpsToDo0)
        gpsToDo0.Clear()
        Console.Write($"Looking up {gpsToDo.Count} GPS places")

        ' Send the request
        Dim queryData = "Bing Spatial Data Services, 2.0" & vbCrLf
        queryData &= "Id|GeocodeRequest/Culture|ReverseGeocodeRequest/IncludeEntityTypes|ReverseGeocodeRequest/Location/Latitude|ReverseGeocodeRequest/Location/Longitude|GeocodeResponse/Address/Neighborhood|GeocodeResponse/Address/Locality|GeocodeResponse/Address/AdminDistrict|GeocodeResponse/Address/CountryRegion" & vbCrLf
        For Each kv In gpsToDo
            queryData &= $"{kv.Key}|en-US|neighborhood|{kv.Value.gpsCoordinates.Latitude:0.000000}|{kv.Value.gpsCoordinates.Longitude:0.000000}{vbCrLf}"
        Next
        Dim key = BingMapsKey.BingMapsKey
        Dim queryUri = $"http://spatial.virtualearth.net/REST/v1/dataflows/geocode?input=pipe&key={key}"
        Console.Write(".")
        Dim statusResp = http.PostAsync(queryUri, New StringContent(queryData)).GetAwaiter().GetResult()
        If Not statusResp.IsSuccessStatusCode Then Console.WriteLine($"ERROR {statusResp.StatusCode} - {statusResp.ReasonPhrase}") : Return
        If String.IsNullOrEmpty(statusResp.Headers.Location?.ToString()) Then Console.WriteLine(" ERROR - no location") : Return
        Dim statusUri = statusResp.Headers.Location.ToString()
        Console.Write(".")

        ' Ping the location until we get somewhere
        Dim resultUri = ""
        While True
            Thread.Sleep(2000)
            Console.Write(".")
            Dim statusRaw = http.GetStringAsync($"{statusUri}?key={key}&output=xml").GetAwaiter().GetResult()
            Console.Write(".")
            Dim statusXml = XDocument.Parse(statusRaw)
            Dim status = statusXml...< Status >.Value
            If status Is Nothing Then Console.WriteLine("ERROR didn't find status") : Return
            If status = "Pending" Then Continue While
            If status = "Failed" Then Console.WriteLine("ERROR 'Failed'") : Return
            resultUri = (From link In statusXml...<Link>
                         Where link.@role = "output" AndAlso link.@name = "succeeded"
                         Select link.Value).FirstOrDefault
            Exit While
        End While
        If String.IsNullOrEmpty(resultUri) Then Console.WriteLine("ERROR no results") : Return

        Dim resultRaw = http.GetStringAsync($"{resultUri}?key={key}&output=json").GetAwaiter().GetResult()
        Console.Write(".")
        Dim resultLines = resultRaw.Split({vbCr(0), vbLf(0)}, StringSplitOptions.RemoveEmptyEntries).Skip(2).ToArray()
        For Each result In resultLines
            Dim parts = result.Split({"|"c})
            Dim parts2 As New List(Of String)
            Dim id = Integer.Parse(parts(0))
            Dim neighborhood = parts(5) ' Capitol Hill
            Dim locality = parts(6) ' Seattle
            Dim adminDistrict = parts(7) ' WA
            Dim country = parts(8) ' United States
            If Not String.IsNullOrEmpty(neighborhood) Then parts2.Add(neighborhood)
            If Not String.IsNullOrEmpty(locality) Then parts2.Add(locality)
            If Not String.IsNullOrEmpty(adminDistrict) Then parts2.Add(adminDistrict)
            If Not String.IsNullOrEmpty(country) Then parts2.Add(country)
            Dim place = String.Join(", ", parts2)
            If Not String.IsNullOrEmpty(place) Then
                gpsToDo(id).hasGpsResult = place
                filesToDo.Enqueue(gpsToDo(id))
            End If
        Next
        Console.WriteLine("done")


    End Sub


    Function FilestampTime(fn As String) As Tuple(Of DateTimeKind, UpdateTimeFunc)
        Dim creationTime = IO.File.GetCreationTime(fn)
        Dim writeTime = IO.File.GetLastWriteTime(fn)
        Dim winnerTime = creationTime
        If writeTime<winnerTime Then winnerTime = writeTime
        Dim localTime = DateTimeKind.Utc(winnerTime.ToUniversalTime()) ' Although they're stored in UTC on disk, the APIs give us local-time
        '
        ' BUG COMPATIBILITY: Filestamp times are never as good as metadata times.
        ' Windows Phone doesn't store metadata, but it does use filenames of the form "WP_20131225".
        ' If we find that, we'll use it.
        Dim year = 0, month = 0, day = 0
        Dim hasWpName = False, usedFilenameTime = False
        If fn Like "*WP_20######*" Then
            Dim i = fn.IndexOf("WP_20") + 3
            If fn.Length >= i + 8 Then
                hasWpName = True
                Dim s = fn.Substring(i, 8)
                year = CInt(s.Substring(0, 4))
                month = CInt(s.Substring(4, 2))
                day = CInt(s.Substring(6, 2))

                If winnerTime.Year = year AndAlso winnerTime.Month = month AndAlso winnerTime.Day = day Then
                    ' good, the filestamp agrees with the filename
                Else
                    localTime = DateTimeKind.Unspecified(New DateTime(year, month, day, 12, 0, 0, System.DateTimeKind.Unspecified))
                    usedFilenameTime = True
                End If
            End If
        End If

        '
        Dim lambda As UpdateTimeFunc =
            Function(file2, off)
                If hasWpName Then
                    Dim nt = winnerTime + off
                    If usedFilenameTime OrElse nt.Year<> year OrElse nt.Month<> month OrElse nt.Day<> day Then
                        Console.WriteLine("Unable to modify time of file, since time was derived from filename") : Return False
                    End If
                End If

                IO.File.SetCreationTime(fn, creationTime + off)
                IO.File.SetLastWriteTime(fn, writeTime + off)
                Return True
            End Function

        Return Tuple.Create(localTime, lambda)
    End Function


    Function MetadataTimeAndGps(fn As String) As Tuple(Of DateTimeKind?, UpdateTimeFunc, GpsCoordinates)
        Using file As New IO.FileStream(fn, IO.FileMode.Open, IO.FileAccess.Read)
            file.Seek(0, IO.SeekOrigin.End) : Dim fend = file.Position
            If fend< 8 Then Return EmptyResult
            file.Seek(0, IO.SeekOrigin.Begin)
            Dim h1 = file.Read2byte(), h2 = file.Read2byte(), h3 = file.Read4byte()
            If h1 = &HFFD8 Then Return ExifTime(file, 0, fend) ' jpeg header
            If h3 = &H66747970 Then Return Mp4Time(file, 0, fend) ' "ftyp" prefix of mp4, mov
            Return Nothing
        End Using
    End Function

    Function ExifTime(file As IO.Stream, start As Long, fend As Long) As Tuple(Of DateTimeKind?, UpdateTimeFunc, GpsCoordinates)
        Dim timeLastModified, timeOriginal, timeDigitized As DateTime?
        Dim posLastModified = 0L, posOriginal = 0L, posDigitized = 0L
        Dim gpsNS = "", gpsEW = "", gpsLatVal As Double? = Nothing, gpsLongVal As Double? = Nothing

        Dim pos = start + 2
        While True ' Iterate through the EXIF markers
            If pos + 4 > fend Then Exit While
            file.Seek(pos, IO.SeekOrigin.Begin)
            Dim marker = file.Read2byte()
            Dim msize = file.Read2byte()
            'Console.WriteLine("EXIF MARKER {0:X}", marker)
            If pos + msize > fend Then Exit While
            Dim mbuf_pos = pos
            pos += 2 + msize
            If marker = &HFFDA Then Exit While ' image data follows this marker; we can stop our iteration
            If marker<> &HFFE1 Then Continue While ' we're only interested in exif markers
            If msize < 14 Then Continue While
            Dim exif1 = file.Read4byte() : If exif1 <> &H45786966 Then Continue While ' exif marker should start with this header "Exif"
            Dim exif2 = file.Read2byte() : If exif2 <> 0 Then Continue While ' and with this header
            Dim exif3 = file.Read4byte()
            Dim ExifDataIsLittleEndian = False
            If exif3 = &H49492A00 Then : ExifDataIsLittleEndian = True
            ElseIf exif3 = &H4D4D002A Then : ExifDataIsLittleEndian = False
            Else : Continue While : End If ' unrecognized byte-order
            Dim ipos = file.Read4byte(ExifDataIsLittleEndian)
            If ipos + 12 >= msize Then Continue While ' error  in tiff header
            '
            ' Format of EXIF is a chain of IFDs. Each consists of a number of tagged entries.
            ' One of the tagged entries may be "SubIFDpos = &H..." which gives the address of the
            ' next IFD in the chain; if this entry is absent or 0, then we're on the last IFD.
            ' Another tagged entry may be "GPSInfo = &H..." which gives the address of the GPS IFD
            '
            Dim subifdpos As UInteger = 0
            Dim gpsifdpos As UInteger = 0
            While True ' iterate through the IFDs
                'Console.WriteLine("  IFD @{0:X}\n", ipos)
                Dim ibuf_pos = mbuf_pos + 10 + ipos
                file.Seek(ibuf_pos, IO.SeekOrigin.Begin)
                Dim nentries = file.Read2byte(ExifDataIsLittleEndian)
                If 10 + ipos + 2 + nentries* 12 + 4 >= msize Then Exit While ' error in ifd header
                file.Seek(ibuf_pos + 2 + nentries* 12, IO.SeekOrigin.Begin)
                ipos = file.Read4byte(ExifDataIsLittleEndian)
                For i = 0 To nentries - 1
                    Dim ebuf_pos = ibuf_pos + 2 + i * 12
                    file.Seek(ebuf_pos, IO.SeekOrigin.Begin)
                    Dim tag = file.Read2byte(ExifDataIsLittleEndian)
                    Dim format = file.Read2byte(ExifDataIsLittleEndian)
                    Dim ncomps = file.Read4byte(ExifDataIsLittleEndian)
                    Dim data = file.Read4byte(ExifDataIsLittleEndian)
                    'Console.WriteLine("    TAG {0:X} format={1:X} ncomps={2:X} data={3:X}", tag, format, ncomps, data)
                    If tag = &H8769 AndAlso format = 4 Then
                        subifdpos = data
                    ElseIf tag = &H8825 AndAlso format = 4 Then
                        gpsifdpos = data
                    ElseIf(tag = 1 OrElse tag = 3) AndAlso format = 2 AndAlso ncomps = 2 Then
                       Dim s = ChrW(CInt(data >> 24))
                        If tag = 1 Then gpsNS = s Else gpsEW = s
                    ElseIf(tag = 2 OrElse tag = 4) AndAlso format = 5 AndAlso ncomps = 3 AndAlso 10 + data + ncomps<msize Then

                       Dim ddpos = mbuf_pos + 10 + data

                       file.Seek(ddpos, IO.SeekOrigin.Begin)
                        Dim degTop = file.Read4byte(ExifDataIsLittleEndian)
                        Dim degBot = file.Read4byte(ExifDataIsLittleEndian)
                        Dim minTop = file.Read4byte(ExifDataIsLittleEndian)
                        Dim minBot = file.Read4byte(ExifDataIsLittleEndian)
                        Dim secTop = file.Read4byte(ExifDataIsLittleEndian)
                        Dim secBot = file.Read4byte(ExifDataIsLittleEndian)
                        Dim deg = degTop / degBot + minTop / minBot / 60.0 + secTop / secBot / 3600.0
                        If tag = 2 Then gpsLatVal = deg
                        If tag = 4 Then gpsLongVal = deg
                    ElseIf(tag = &H132 OrElse tag = &H9003 OrElse tag = &H9004) AndAlso format = 2 AndAlso ncomps = 20 AndAlso 10 + data + ncomps<msize Then

                       Dim ddpos = mbuf_pos + 10 + data

                       file.Seek(ddpos, IO.SeekOrigin.Begin)
                        Dim buf(18) As Byte : file.Read(buf, 0, 19)
                        Dim s = Text.Encoding.ASCII.GetString(buf)
                        Dim dd As DateTime
                        If DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None, dd) Then
                            If tag = &H132 Then timeLastModified = dd : posLastModified = ddpos
                            If tag = &H9003 Then timeOriginal = dd : posOriginal = ddpos
                            If tag = &H9004 Then timeDigitized = dd : posDigitized = ddpos
                            'Console.WriteLine("      {0}", dd)
                        End If
                    End If
                Next
                If ipos = 0 Then
                    ipos = subifdpos : subifdpos = 0
                    If ipos = 0 Then ipos = gpsifdpos : gpsifdpos = 0
                    If ipos = 0 Then Exit While ' indicates the last IFD in this marker
                End If
            End While
        End While

        Dim winnerTime = timeLastModified
        If Not winnerTime.HasValue OrElse(timeDigitized.HasValue AndAlso timeDigitized.Value<winnerTime.Value) Then winnerTime = timeDigitized
        If Not winnerTime.HasValue OrElse(timeOriginal.HasValue AndAlso timeOriginal.Value<winnerTime.Value) Then winnerTime = timeOriginal
        '
        Dim winnerTimeOffset = If(winnerTime.HasValue, DateTimeKind.Unspecified(winnerTime.Value), CType(Nothing, DateTimeKind ?))

        Dim lambda As UpdateTimeFunc =
            Function(file2, off)
                If timeLastModified.HasValue AndAlso posLastModified <> 0 Then
                    Dim buf = Text.Encoding.ASCII.GetBytes((timeLastModified.Value + off).ToString("yyyy:MM:dd HH:mm:ss"))
                    file2.Seek(posLastModified, IO.SeekOrigin.Begin) : file2.Write(buf, 0, buf.Length)
                End If
                If timeOriginal.HasValue AndAlso posOriginal <> 0 Then
                    Dim buf = Text.Encoding.ASCII.GetBytes((timeOriginal.Value + off).ToString("yyyy:MM:dd HH:mm:ss"))
                    file2.Seek(posOriginal, IO.SeekOrigin.Begin) : file2.Write(buf, 0, buf.Length)
                End If
                If timeDigitized.HasValue AndAlso posDigitized <> 0 Then
                    Dim buf = Text.Encoding.ASCII.GetBytes((timeDigitized.Value + off).ToString("yyyy:MM:dd HH:mm:ss"))
                    file2.Seek(posDigitized, IO.SeekOrigin.Begin) : file2.Write(buf, 0, buf.Length)
                End If
                Return True
            End Function

        Dim gps As GpsCoordinates = Nothing
        If(gpsNS = "N" OrElse gpsNS = "S") AndAlso gpsLatVal.HasValue AndAlso (gpsEW = "E" OrElse gpsEW = "W") AndAlso gpsLongVal.HasValue Then
           gps = New GpsCoordinates
           gps.Latitude = If(gpsNS = "N", gpsLatVal.Value, -gpsLatVal.Value)

           gps.Longitude = If(gpsEW = "E", gpsLongVal.Value, -gpsLongVal.Value)

       End If


       Return Tuple.Create(winnerTimeOffset, lambda, gps)
    End Function

    Function Mp4Time(file As IO.Stream, start As Long, fend As Long) As Tuple(Of DateTimeKind?, UpdateTimeFunc, GpsCoordinates)
        ' The file is made up of a sequence of boxes, with a standard way to find size and FourCC "kind" of each.
        ' Some box kinds contain a kind-specific blob of binary data. Other box kinds contain a sequence
        ' of sub-boxes. You need to look up the specs for each kind to know whether it has a blob or sub-boxes.
        ' We look for a top-level box of kind "moov", which contains sub-boxes, and then we look for its sub-box
        ' of kind "mvhd", which contains a binary blob. This is where Creation/ModificationTime are stored.
        Dim pos = start, payloadStart = 0L, payloadEnd = 0L, boxKind = ""
        '
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "ftyp" : pos = payloadEnd
        End While
        If boxKind <> "ftyp" Then Return EmptyResult
        Dim majorBrandBuf(3) As Byte
        file.Seek(payloadStart, IO.SeekOrigin.Begin) : file.Read(majorBrandBuf, 0, 4)
        Dim majorBrand = Text.Encoding.ASCII.GetString(majorBrandBuf)
        '
        pos = start
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "moov" : pos = payloadEnd : End While
        If boxKind <> "moov" Then Return EmptyResult
        Dim moovStart = payloadStart, moovEnd = payloadEnd
        '
        pos = moovStart : fend = moovEnd
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "mvhd" : pos = payloadEnd : End While
        If boxKind <> "mvhd" Then Return EmptyResult
        Dim mvhdStart = payloadStart, mvhdEnd = payloadEnd
        '
        pos = moovStart : fend = moovEnd
        Dim cdayStart = 0L, cdayEnd = 0L
        Dim cnthStart = 0L, cnthEnd = 0L
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "udta" : pos = payloadEnd : End While
        If boxKind = "udta" Then
            Dim udtaStart = payloadStart, udtaEnd = payloadEnd
            '
            pos = udtaStart : fend = udtaEnd
            While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "©day" : pos = payloadEnd : End While
            If boxKind = "©day" Then cdayStart = payloadStart : cdayEnd = payloadEnd
            '
            pos = udtaStart : fend = udtaEnd
            While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind<> "CNTH" : pos = payloadEnd : End While
            If boxKind = "CNTH" Then cnthStart = payloadStart : cnthEnd = payloadEnd
        End If

        ' The "mvhd" binary blob consists of 1byte (version, either 0 or 1), 3bytes (flags),
        ' and then either 4bytes (creation), 4bytes (modification)
        ' or 8bytes (creation), 8bytes (modification)
        ' If version=0 then it's the former, otherwise it's the later.
        ' In both cases "creation" and "modification" are big-endian number of seconds since 1st Jan 1904 UTC
        If mvhdEnd - mvhdStart< 20 Then Return EmptyResult
        file.Seek(mvhdStart + 0, IO.SeekOrigin.Begin) : Dim version = file.ReadByte(), numBytes = If(version = 0, 4, 8)
        file.Seek(mvhdStart + 4, IO.SeekOrigin.Begin)
        Dim creationFix1970 = False, modificationFix1970 = False
        Dim creationTime = file.ReadDate(numBytes, creationFix1970)
        Dim modificationTime = file.ReadDate(numBytes, modificationFix1970)
        ' COMPATIBILITY-BUG: The spec says that these times are in UTC.
        ' However, my Sony Cybershot merely gives them in unspecified time (i.e. local time but without specifying the timezone)
        ' Indeed its UI doesn't even let you say what the current UTC time is.
        ' I also noticed that my Sony Cybershot gives MajorBrand="MSNV", which isn't used by my iPhone or Canon or WP8.
        ' I'm going to guess that all "MSNV" files come from Sony, and all of them have the bug.
        Dim makeMvhdTime = Function(dt As DateTime) As DateTimeKind
                               If majorBrand = "MSNV" Then Return DateTimeKind.Unspecified(dt)
                               Return DateTimeKind.Utc(dt)
                           End Function

        ' The "©day" binary blob consists of 2byte (string-length, big-endian), 2bytes (language-code), string
        Dim dayTime As DateTimeKind? = Nothing
        Dim cdayStringLen = 0, cdayString = ""
        If cdayStart<> 0 AndAlso cdayEnd - cdayStart > 4 Then
            file.Seek(cdayStart + 0, IO.SeekOrigin.Begin)
            cdayStringLen = file.Read2byte()
            If cdayStart + 4 + cdayStringLen <= cdayEnd Then
                file.Seek(cdayStart + 4, IO.SeekOrigin.Begin)
                Dim buf = New Byte(cdayStringLen - 1) { }
file.Read(buf, 0, cdayStringLen)
                cdayString = System.Text.Encoding.ASCII.GetString(buf)
                Dim d As DateTimeOffset : If DateTimeOffset.TryParse(cdayString, d) Then dayTime = DateTimeKind.Local(d)
            End If
        End If

        ' The "CNTH" binary blob consists of 8bytes of unknown, followed by EXIF data
        Dim cnthTime As DateTimeKind? = Nothing, cnthLambda As UpdateTimeFunc = Nothing
        If cnthStart<> 0 AndAlso cnthEnd - cnthStart > 16 Then
           Dim exif_ft = ExifTime(file, cnthStart + 8, cnthEnd)
            cnthTime = exif_ft.Item1 : cnthLambda = exif_ft.Item2
        End If

        Dim winnerTime As DateTimeKind? = Nothing
        If dayTime.HasValue Then
            Debug.Assert(dayTime.Value.dt.Kind = System.DateTimeKind.Local)
            winnerTime = dayTime
            ' prefer this best of all because it knows local time and timezone
        ElseIf cnthTime.HasValue Then
            Debug.Assert(cnthTime.Value.dt.Kind = System.DateTimeKind.Unspecified)
            winnerTime = cnthTime
            ' this is second-best because it knows local time, just not timezone
        Else
            ' Otherwise, we'll make do with a UTC time, where we don't know local-time when the pic was taken, nor timezone
            If creationTime.HasValue AndAlso modificationTime.HasValue Then
                winnerTime = makeMvhdTime(If(creationTime < modificationTime, creationTime.Value, modificationTime.Value))
            ElseIf creationTime.HasValue Then
                winnerTime = makeMvhdTime(creationTime.Value)
            ElseIf modificationTime.HasValue Then
                winnerTime = makeMvhdTime(modificationTime.Value)
            End If
        End If

        Dim lambda As UpdateTimeFunc =
            Function(file2, offset)
                If creationTime.HasValue Then
                    Dim dd = creationTime.Value + offset
                    file2.Seek(mvhdStart + 4, IO.SeekOrigin.Begin)
                    file2.WriteDate(numBytes, dd, creationFix1970)
                End If
                If modificationTime.HasValue Then
                    Dim dd = modificationTime.Value + offset
                    file2.Seek(mvhdStart + 4 + numBytes, IO.SeekOrigin.Begin)
                    file2.WriteDate(numBytes, dd, modificationFix1970)
                End If
                If Not String.IsNullOrWhiteSpace(cdayString) Then
                    Dim dd As DateTimeOffset
                    If DateTimeOffset.TryParse(cdayString, dd) Then
                        dd = dd + offset
                        Dim str2 = dd.ToString("yyyy-MM-ddTHH:mm:sszz00")
                        Dim buf2 = Text.Encoding.ASCII.GetBytes(str2)
                        If buf2.Length = cdayStringLen Then
                            file2.Seek(cdayStart + 4, IO.SeekOrigin.Begin)
                            file2.Write(buf2, 0, buf2.Length)
                        End If
                    End If
                End If
                If cnthLambda IsNot Nothing Then cnthLambda(file2, offset)
                Return True
            End Function

        Return Tuple.Create(winnerTime, lambda, CType(Nothing, GpsCoordinates))
    End Function


    Function Mp4ReadNextBoxInfo(f As IO.Stream, pos As Long, fend As Long, ByRef boxKind As String, ByRef payloadStart As Long, ByRef payloadEnd As Long) As Boolean
        boxKind = "" : payloadStart = 0 : payloadEnd = 0
        If pos + 8 > fend Then Return False
        Dim b(3) As Byte
        f.Seek(pos, IO.SeekOrigin.Begin)
        f.Read(b, 0, 4) : If BitConverter.IsLittleEndian Then Array.Reverse(b)
        Dim size = BitConverter.ToUInt32(b, 0)
        f.Read(b, 0, 4)
        Dim kind = ChrW(b(0)) & ChrW(b(1)) & ChrW(b(2)) & ChrW(b(3))
        If size<> 1 Then
            If pos + size > fend Then Return False
            boxKind = kind : payloadStart = pos + 8 : payloadEnd = payloadStart + size - 8 : Return True
        End If
        If size = 1 AndAlso pos + 16 <= fend Then
            ReDim b(7)
            f.Read(b, 0, 8) : If BitConverter.IsLittleEndian Then Array.Reverse(b)
            Dim size2 = CLng(BitConverter.ToUInt64(b, 0))
            If pos + size2 > fend Then Return False
            boxKind = kind : payloadStart = pos + 16 : payloadEnd = payloadStart + size2 - 16 : Return True
        End If
        Return False
    End Function

    <Runtime.CompilerServices.Extension>
    Sub Add(Of T, U, V)(this As LinkedList(Of Tuple(Of T, U, V)), arg1 As T, arg2 As U, arg3 As V)
        this.AddLast(Tuple.Create(Of T, U, V)(arg1, arg2, arg3))
    End Sub

    ReadOnly TZERO_1904_UTC As DateTime = New DateTime(1904, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)
    ReadOnly TZERO_1970_UTC As DateTime = New DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)

    <Runtime.CompilerServices.Extension>
    Function Read2byte(f As IO.Stream, Optional fileIsLittleEndian As Boolean = False) As UShort
        Dim b(1) As Byte
        f.Read(b, 0, 2) : If BitConverter.IsLittleEndian <> fileIsLittleEndian Then Array.Reverse(b)
        Return BitConverter.ToUInt16(b, 0)
    End Function

    <Runtime.CompilerServices.Extension>
    Function Read4byte(f As IO.Stream, Optional fileIsLittleEndian As Boolean = False) As UInteger
        Dim b(3) As Byte
        f.Read(b, 0, 4) : If BitConverter.IsLittleEndian <> fileIsLittleEndian Then Array.Reverse(b)
        Return BitConverter.ToUInt32(b, 0)
    End Function

    <Runtime.CompilerServices.Extension>
    Function ReadDate(f As IO.Stream, numBytes As Integer, ByRef fixed1970 As Boolean) As DateTime?
        ' COMPATIBILITY-BUG: The spec says that these are expressed in seconds since 1904.
        ' But my brother's Android phone picks them in seconds since 1970.
        ' I'm going to guess that all dates before 1970 should be 66 years in the future
        ' Note: I'm applying this correction *before* converting to date. That's because,
        ' what with leap-years and stuff, it doesn't feel safe the other way around.
        If numBytes = 4 Then
            Dim b(3) As Byte
            f.Read(b, 0, 4) : If BitConverter.IsLittleEndian Then Array.Reverse(b)
            Dim secs = BitConverter.ToUInt32(b, 0)
            If secs = 0 Then Return Nothing
            fixed1970 = (secs < (TZERO_1970_UTC - TZERO_1904_UTC).TotalSeconds)
            Return If(fixed1970, TZERO_1970_UTC.AddSeconds(secs), TZERO_1904_UTC.AddSeconds(secs))
        ElseIf numBytes = 8 Then
            Dim b(7) As Byte
            f.Read(b, 0, 8) : If BitConverter.IsLittleEndian Then Array.Reverse(b)
            Dim secs = BitConverter.ToUInt64(b, 0)
            If secs = 0 Then Return Nothing
            fixed1970 = (secs < (TZERO_1970_UTC - TZERO_1904_UTC).TotalSeconds)
            Return If(fixed1970, TZERO_1970_UTC.AddSeconds(secs), TZERO_1904_UTC.AddSeconds(secs))
        Else
            Throw New ArgumentException("numBytes")
        End If
    End Function

    <Runtime.CompilerServices.Extension>
    Sub WriteDate(f As IO.Stream, numBytes As Integer, d As DateTime, fix1970 As Boolean)
        If d.Kind <> System.DateTimeKind.Utc Then Throw New ArgumentException("Can only write UTC dates")
        If numBytes = 4 Then
            Dim secs = CUInt(If(fix1970, d - TZERO_1970_UTC, d - TZERO_1904_UTC).TotalSeconds)
            Dim b = BitConverter.GetBytes(secs) : If BitConverter.IsLittleEndian Then Array.Reverse(b)
            f.Write(b, 0, 4)
        ElseIf numBytes = 8 Then
            Dim secs = CULng(If(fix1970, d - TZERO_1970_UTC, d - TZERO_1904_UTC).TotalSeconds)
            Dim b = BitConverter.GetBytes(secs) : If BitConverter.IsLittleEndian Then Array.Reverse(b)
            f.Write(b, 0, 8)
        Else
            Throw New ArgumentException("numBytes")
        End If
    End Sub

    Structure DateTimeKind
        Public dt As DateTime
        Public offset As TimeSpan
        ' Three modes:
        ' (1) Time known to be in UTC: DateTime.Kind=UTC, offset=0
        ' (2) Time known to be in some specific timezone: DateTime.Kind=Local, offset gives that timezone
        ' (3) Time where nothing about timezone is known: DateTime.Kind=Unspecified, offset=0

        Shared Function Utc(d As DateTime) As DateTimeKind
            Dim d2 As New DateTime(d.Ticks, System.DateTimeKind.Utc)
            Return New DateTimeKind With {.dt = d2, .offset = Nothing}
End Function
        Shared Function Unspecified(d As DateTime) As DateTimeKind
            Dim d2 As New DateTime(d.Ticks, System.DateTimeKind.Unspecified)
            Return New DateTimeKind With {.dt = d2, .offset = Nothing}
End Function
        Shared Function Local(d As DateTimeOffset) As DateTimeKind
            Dim d2 As New DateTime(d.Ticks, System.DateTimeKind.Local)
            Return New DateTimeKind With {.dt = d2, .offset = d.Offset}
End Function

        Public Overrides Function ToString() As String
            If dt.Kind = System.DateTimeKind.Utc Then
                Return dt.ToString("yyyy:MM:ddTHH:mm:ssZ")
            ElseIf dt.Kind = System.DateTimeKind.Unspecified Then
                Return dt.ToString("yyyy:MM:dd HH:mm:ss")
            ElseIf dt.Kind = System.DateTimeKind.Local Then
                Return dt.ToString("yyyy:MM:dd HH:mm:ss") & offset.Hours.ToString("+00;-00") & "00"
            Else
                Throw New Exception("Invalid DateTimeKind")
            End If
        End Function
    End Structure


    Public Class AsyncPump
        Public Shared Sub Run(func As Func(Of Task))
            Dim prevCtx = SynchronizationContext.Current
            Try
                Dim syncCtx As New SingleThreadSynchronizationContext()
                SynchronizationContext.SetSynchronizationContext(syncCtx)
                Dim t = func()
                If t Is Nothing Then Throw New InvalidOperationException("No task provided.")
                t.ContinueWith(Sub() syncCtx.Complete(), TaskScheduler.Default)
                syncCtx.RunOnCurrentThread()
                t.GetAwaiter().GetResult()
            Finally
                SynchronizationContext.SetSynchronizationContext(prevCtx)
            End Try
        End Sub

        Private NotInheritable Class SingleThreadSynchronizationContext : Inherits SynchronizationContext
            Private ReadOnly m_queue As New Concurrent.BlockingCollection(Of Tuple(Of SendOrPostCallback, Object))
            Private ReadOnly m_thread As Thread = Thread.CurrentThread
            Public Overrides Sub Post(d As SendOrPostCallback, state As Object)
                m_queue.Add(Tuple.Create(d, state))
            End Sub
            Public Overrides Sub Send(d As SendOrPostCallback, state As Object)
                Throw New NotSupportedException("Synchronously sending is not supported.")
            End Sub
            Public Sub RunOnCurrentThread()
                For Each workItem In m_queue.GetConsumingEnumerable()
                    workItem.Item1.Invoke(workItem.Item2)
                Next
            End Sub
            Public Sub Complete()
                m_queue.CompleteAdding()
            End Sub
        End Class
    End Class


End Module

*/
