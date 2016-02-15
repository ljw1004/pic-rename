Option Strict On

Module Module1

    Sub Test()
        For Each fn In {"eg-android - 2013.12.28 - 15.48 PST.jpg", "eg-android - 2013.12.28 - 15.48 PST.mp4",
                        "eg-canon-ixus - 2013.12.15 - 07.30 PST.jpg", "eg-canon-ixus - 2013.12.15 - 07.30 PST.mov",
                        "eg-canon-powershot - 2013.12.28 - 15.51 PST.jpg", "eg-canon-powershot - 2013.12.28 - 15.51 PST.mov",
                        "eg-iphone4s - 2013.12.28 - 15.49 PST.jpg", "eg-iphone4s - 2013.12.28 - 15.49 PST.mov",
                        "eg-iphone5 - 2013.12.10 - 15.40 PST.jpg", "eg-iphone5 - 2013.12.09 - 15.21 PST.mov",
                        "eg-sony-cybershot - 2013.12.15 - 07.30 PST.jpg", "eg-sony-cybershot - 2013.12.15 - 07.30 PST.mp4",
                        "eg-wp8 - 2013.12.15 - 07.33 PST.jpg", "eg-wp8 - 2013.12.15 - 07.33 PST.mp4",
                        "eg-screenshot.png", "eg-notapic.txt"}
            Dim ft = FilestampTime($"test\{fn}")?.Item1
            Dim mt = MetadataTime($"test\{fn}")?.Item1
            Console.WriteLine($"{fn}{vbCrLf}    ft={ft}{vbCrLf}    mt={mt}")
        Next
    End Sub

    Sub Main(args As String())
        Test() : Return
        ' Goals:
        ' (1) If you have shots from one or more devices, rename them to local time when the shot was taken
        ' (2) If you had taken shots on holiday without having fixed the timezone on your camera, fix their metadata timestamps
        ' also...
        ' (3) Just display the time from a given shot

        ' METADATA DATES:
        ' JPEG:       metadata dates are "unspecified", i.e. local to the time the photo was taken, but without saying which timezone that was
        ' MOV-iphone: metadata dates are "local", i.e. local to the time the video was taken, and they do say which timezone
        ' MOV-canon:  metadata dates officially are "UTC", but we can also find undocumented the "unspecified" version
        ' MP4-sony:   metadata dates claim to be UTC, but in reality they're unspecified, i.e. local to the time the video was taken, and we don't have information about what timezone
        ' MP4-android:metadata dates are in UTC as they claim, but have the "year" 66 years in the past
        ' MP4-wp8:    metadata dates are absent.
        '
        ' FILESTAMP DATES:
        ' In all cases, filestamp dates are UTC.
        ' In some cases they're the filestamp from when the photo/video was taken and written immediately to SD card
        ' In other cases they're the filestamp from when the photo/video was uploaded to Skydrive
        ' In other cases they're the filestamp from when the photo/video was last copied to the folder

        ' Our objective is to rename the file according to "local" time when the photo/video was taken, without the timezone information.

        Dim cmdFn = "", cmdPattern = "", cmdOffset As TimeSpan? = Nothing, cmdError = ""
        Dim cmdArgs = New LinkedList(Of String)(args)
        ' Get the filename
        If cmdArgs.Count > 0 AndAlso Not cmdArgs.First.Value.StartsWith("/") Then cmdFn = cmdArgs.First.Value : cmdArgs.RemoveFirst()
        ' Search for further switches
        While cmdError = "" AndAlso cmdArgs.Count > 0
            Dim switch = cmdArgs.First.Value : cmdArgs.RemoveFirst()
            If switch = "/rename" Then
                If cmdPattern <> "" Then cmdError = "duplicate /rename" : Exit While
                cmdPattern = "%{datetime} - %{fn}"
                If cmdArgs.Count > 0 AndAlso Not cmdArgs.First.Value.StartsWith("/") Then cmdPattern = cmdArgs.First.Value : cmdArgs.RemoveFirst()
            ElseIf switch.StartsWith("/day") OrElse switch.StartsWith("/hour") OrElse switch.StartsWith("/minute") Then
                Dim len = 0, mkts As Func(Of Integer, TimeSpan) = Function(n) Nothing
                If switch.StartsWith("/day") Then len = 4 : mkts = Function(n) TimeSpan.FromDays(n)
                If switch.StartsWith("/hour") Then len = 5 : mkts = Function(n) TimeSpan.FromHours(n)
                If switch.StartsWith("/minute") Then len = 7 : mkts = Function(n) TimeSpan.FromMinutes(n)
                Dim snum = switch.Substring(len)
                If Not snum.StartsWith("+") AndAlso Not snum.StartsWith("-") Then cmdError = switch : Exit While
                Dim num = 0 : If Not Integer.TryParse(snum, num) Then cmdError = switch : Exit While
                cmdOffset = If(cmdOffset.HasValue, cmdOffset, New TimeSpan(0))
                cmdOffset = cmdOffset + mkts(num)
            ElseIf switch = "/?" Then
                cmdFn = ""
            Else
                cmdError = switch : Exit While
            End If
        End While
        If cmdError <> "" Then Console.WriteLine("Unrecognized command: {0}", cmdError) : Return
        If cmdArgs.Count > 0 Then Throw New Exception("Failed to parse command line")
        If cmdFn = "" Then
            Console.WriteLine("FixCameraDate ""a.jpg"" [/rename [""pattern""]] [/day+n] [/hour+n] [/minute+n]")
            Console.WriteLine("  Filename can include * and ? wildcards")
            Console.WriteLine("  /rename: pattern defaults to ""%{datetime} - %{fn}"" and")
            Console.WriteLine("           can include %{date/time/year/month/day/hour/minute/second}")
            Console.WriteLine("  /day,/hour,/minute: adjust the timestamp; can be + or -")
            Console.WriteLine()
            Console.WriteLine("EXAMPLES:")
            Console.WriteLine("FixCameraDate ""a.jpg""")
            Console.WriteLine("FixCameraDate ""*.jpg"" /rename ""%{date} - %{time} - %{fn}.jpg""")
            Console.WriteLine("FixCameraDate ""*D*.mov"" /hour+8 /rename")
            Return
        End If

        Dim globPath = "", globMatch = cmdFn
        If globMatch.Contains("\") Then
            globPath = IO.Path.GetDirectoryName(globMatch) : globMatch = IO.Path.GetFileName(globMatch)
        Else
            globPath = Environment.CurrentDirectory
        End If
        Dim globFiles = IO.Directory.GetFiles(globPath, globMatch)
        If globFiles.Length = 0 Then Console.WriteLine("Not found - ""{0}""", cmdFn)
        For Each globFile In globFiles
            Dim origFn = globFile

            Dim mtt = MetadataTime(origFn)
            Dim ftt = FilestampTime(origFn)
            If mtt Is Nothing Then Console.WriteLine("Not an image/video - ""{0}""", IO.Path.GetFileName(origFn)) : Continue For
            Dim mt = mtt.Item1, ft = ftt.Item1
            Dim setter As UpdateTimeFunc = Nothing
            Dim localTime As DateTime
            If mt.HasValue Then
                setter = mtt.Item2
                If mt.Value.dt.Kind = System.DateTimeKind.Unspecified OrElse mt.Value.dt.Kind = System.DateTimeKind.Local Then
                    ' If dt.kind=Unspecified (e.g. EXIF, Sony), then the time is by assumption already local from when the picture was shot
                    ' If dt.kind=Local (e.g. iPhone-MOV), then the time is local, and also indicates its timezone offset
                    localTime = mt.Value.dt
                ElseIf mt.Value.dt.Kind = System.DateTimeKind.Utc Then
                    ' If dt.Kind=UTC (e.g. Android), then time is in UTC, and we don't know how to read timezone.
                    localTime = mt.Value.dt.ToLocalTime() ' Best we can do is guess the timezone of the computer
                End If
            Else
                setter = ftt.Item2
                If ft.dt.Kind = System.DateTimeKind.Unspecified Then
                    ' e.g. Windows Phone when we got the date from the filename
                    localTime = ft.dt
                ElseIf ft.dt.Kind = System.DateTimeKind.Utc Then
                    ' e.g. all other files where we got the date from the filestamp
                    localTime = ft.dt.ToLocalTime() ' the best we can do is guess that the photo was taken in the timezone as this computer now
                Else
                    Throw New Exception("Expected filetimes to be in UTC")
                End If
            End If


            If cmdPattern = "" AndAlso Not cmdOffset.HasValue Then
                Console.WriteLine("""{0}"": {1:yyyy.MM.dd - HH.mm.ss}", IO.Path.GetFileName(origFn), localTime)
            End If


            If cmdOffset.HasValue Then
                Using file = New IO.FileStream(origFn, IO.FileMode.Open, IO.FileAccess.ReadWrite)
                    Dim prevTime = localTime
                    Dim r = setter(file, cmdOffset.Value)
                    If r Then
                        localTime += cmdOffset.Value
                        If cmdPattern = "" Then Console.WriteLine("""{0}"": {1:yyyy.MM.dd - HH.mm.ss}, corrected from {2:yyyy.MM.dd - HH.mm.ss}", IO.Path.GetFileName(origFn), localTime, prevTime)
                    End If
                End Using
            End If


            If cmdPattern <> "" Then
                ' Filename heuristics:
                ' (1) If the user omitted an extension from the rename string, then we re-use the one that was given to us
                ' (2) If the filename already matched our datetime format, then we figure out what was the base filename
                If Not cmdPattern.Contains("%{datetime}") AndAlso Not cmdPattern.Contains("%{date}") Then Console.WriteLine("Please include either %{datetime} or %{date} in the pattern") : Return
                If Not cmdPattern.Contains("%{fn}") Then Console.WriteLine("Please include %{fn} in the pattern") : Return
                If cmdPattern.Contains("\") Then Console.WriteLine("Folders not allowed in pattern") : Return
                If cmdPattern.Split({"%{fn}"}, StringSplitOptions.None).Length <> 2 Then Console.WriteLine("Please include %{fn} only once in the pattern") : Return
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
                If patternExt Is Nothing Then patternExt = IO.Path.GetExtension(origFn)
                '
                ' 2. Parse the pattern-string into its constitutent parts
                Dim patternSplit = pattern.Split({"%"c})
                For i = 1 To patternSplit.Length - 1 : patternSplit(i) = "%" & patternSplit(i) : Next
                Dim patternParts As New LinkedList(Of Tuple(Of Func(Of String, DateTime, String), Integer, Func(Of String, Boolean)))
                For Each rsplit In patternSplit
                    Dim remainder = ""
                    If Not rsplit.StartsWith("%") Then
                        If rsplit.Length > 0 Then patternParts.Add(Function() rsplit, rsplit.Length, Function(s) s = rsplit)
                        Continue For
                    End If
                    If rsplit.StartsWith("%{fn}") Then
                        patternParts.Add(Function(fn2, dt2) fn2, -1, Function() False)
                        remainder = rsplit.Substring(5)
                        If remainder.Length > 0 Then patternParts.Add(Function() remainder, remainder.Length, Function(s) s = remainder)
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
                    patternParts.Add(Function(fn2, dt2) dt2.ToString(fmt), islike.Length, Function(s) s Like islike)
                    remainder = rsplit.Substring(escape.Length)
                    If remainder.Length > 0 Then patternParts.Add(Function() remainder, remainder.Length, Function(s) s = remainder)
                Next
                '
                ' 3. Attempt to match the existing filename against the pattern
                Dim basefn = IO.Path.GetFileNameWithoutExtension(origFn)
                Dim matchfn = basefn
                Dim matchParts As New LinkedList(Of Tuple(Of Func(Of String, DateTime, String), Integer, Func(Of String, Boolean)))(patternParts)
                While matchParts.Count > 1 AndAlso matchfn IsNot Nothing
                    Dim patternPart As Tuple(Of Func(Of String, DateTime, String), Integer, Func(Of String, Boolean)) = Nothing, atStart = True
                    If matchParts.First.Value.Item2 <> -1 Then
                        patternPart = matchParts.First.Value : matchParts.RemoveFirst() : atStart = True
                    Else
                        patternPart = matchParts.Last.Value : matchParts.RemoveLast() : atStart = False
                    End If
                    Dim partLen = patternPart.Item2
                    If matchfn.Length < partLen Then matchfn = Nothing : Exit While
                    Dim match = If(atStart, matchfn.Substring(0, partLen), matchfn.Substring(matchfn.Length - partLen))
                    If Not patternPart.Item3(match) Then matchfn = Nothing : Exit While
                    matchfn = If(atStart, matchfn.Substring(partLen), matchfn.Substring(0, matchfn.Length - partLen))
                End While
                If matchParts.Count = 1 AndAlso matchParts.First.Value.Item2 = -1 AndAlso matchfn IsNot Nothing Then
                    basefn = matchfn
                End If
                '
                ' 4. Figure out the new filename
                Dim newfn = IO.Path.GetDirectoryName(origFn) & "\"
                For Each patternPart In patternParts : newfn &= patternPart.Item1(basefn, localTime) : Next
                newfn &= patternExt
                If origFn <> newfn Then
                    Console.WriteLine(IO.Path.GetFileName(newfn))
                    IO.File.Move(origFn, newfn)
                End If
            End If

        Next

    End Sub

    Delegate Function UpdateTimeFunc(stream As IO.Stream, off As TimeSpan) As Boolean

    ReadOnly EmptyResult As New Tuple(Of DateTimeKind?, UpdateTimeFunc)(Nothing, Function() False)

    Function FilestampTime(fn As String) As Tuple(Of DateTimeKind, UpdateTimeFunc)
        Dim creationTime = IO.File.GetCreationTime(fn)
        Dim writeTime = IO.File.GetLastWriteTime(fn)
        Dim winnerTime = creationTime
        If writeTime < winnerTime Then winnerTime = writeTime
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
                    If usedFilenameTime OrElse nt.Year <> year OrElse nt.Month <> month OrElse nt.Day <> day Then
                        Console.WriteLine("Unable to modify time of file, since time was derived from filename") : Return False
                    End If
                End If

                IO.File.SetCreationTime(fn, creationTime + off)
                IO.File.SetLastWriteTime(fn, writeTime + off)
                Return True
            End Function

        Return Tuple.Create(localTime, lambda)
    End Function

    Function MetadataTime(fn As String) As Tuple(Of DateTimeKind?, UpdateTimeFunc)
        Using file As New IO.FileStream(fn, IO.FileMode.Open)
            file.Seek(0, IO.SeekOrigin.End) : Dim fend = file.Position
            If fend < 8 Then Return EmptyResult
            file.Seek(0, IO.SeekOrigin.Begin)
            Dim h1 = file.Read2byte(), h2 = file.Read2byte(), h3 = file.Read4byte()
            If h1 = &HFFD8 Then Return ExifTime(file, 0, fend)
            If h3 = &H66747970 Then Return Mp4Time(file, 0, fend)
            Return Nothing
        End Using
    End Function

    Function ExifTime(file As IO.Stream, start As Long, fend As Long) As Tuple(Of DateTimeKind?, UpdateTimeFunc)
        Dim timeLastModified, timeOriginal, timeDigitized As DateTime?
        Dim posLastModified = 0L, posOriginal = 0L, posDigitized = 0L

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
            If marker <> &HFFE1 Then Continue While ' we're only interested in exif markers
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
            '
            Dim subifdpos As UInteger = 0
            While True ' iterate through the IFDs
                'Console.WriteLine("  IFD @{0:X}\n", ipos)
                Dim ibuf_pos = mbuf_pos + 10 + ipos
                file.Seek(ibuf_pos, IO.SeekOrigin.Begin)
                Dim nentries = file.Read2byte(ExifDataIsLittleEndian)
                If 10 + ipos + 2 + nentries * 12 + 4 >= msize Then Exit While ' error in ifd header
                file.Seek(ibuf_pos + 2 + nentries * 12, IO.SeekOrigin.Begin)
                ipos = file.Read4byte(ExifDataIsLittleEndian)
                For i = 0 To nentries - 1
                    Dim ebuf_pos = ibuf_pos + 2 + i * 12
                    file.Seek(ebuf_pos, IO.SeekOrigin.Begin)
                    Dim tag = file.Read2byte(ExifDataIsLittleEndian)
                    Dim format = file.Read2byte(ExifDataIsLittleEndian)
                    Dim ncomps = file.Read4byte(ExifDataIsLittleEndian)
                    Dim data = file.Read4byte(ExifDataIsLittleEndian)
                    If tag = &H8769 Then subifdpos = data
                    'Console.WriteLine("    TAG {0:X} format={1:X} ncomps={2:X} data={3:X}", tag, format, ncomps, data)
                    If (tag = &H132 OrElse tag = &H9003 OrElse tag = &H9004) AndAlso format = 2 AndAlso ncomps = 20 AndAlso 10 + data + ncomps < msize Then
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
                    If ipos = 0 Then Exit While ' indicates the last IFD in this marker
                End If
            End While
        End While

        Dim winnerTime = timeLastModified
        If Not winnerTime.HasValue OrElse (timeDigitized.HasValue AndAlso timeDigitized.Value < winnerTime.Value) Then winnerTime = timeDigitized
        If Not winnerTime.HasValue OrElse (timeOriginal.HasValue AndAlso timeOriginal.Value < winnerTime.Value) Then winnerTime = timeOriginal
        '
        Dim winnerTimeOffset = If(winnerTime.HasValue, DateTimeKind.Unspecified(winnerTime.Value), CType(Nothing, DateTimeKind?))

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

        Return Tuple.Create(winnerTimeOffset, lambda)
    End Function

    Function Mp4Time(file As IO.Stream, start As Long, fend As Long) As Tuple(Of DateTimeKind?, UpdateTimeFunc)
        ' The file is made up of a sequence of boxes, with a standard way to find size and FourCC "kind" of each.
        ' Some box kinds contain a kind-specific blob of binary data. Other box kinds contain a sequence
        ' of sub-boxes. You need to look up the specs for each kind to know whether it has a blob or sub-boxes.
        ' We look for a top-level box of kind "moov", which contains sub-boxes, and then we look for its sub-box
        ' of kind "mvhd", which contains a binary blob. This is where Creation/ModificationTime are stored.
        Dim pos = start, payloadStart = 0L, payloadEnd = 0L, boxKind = ""
        '
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind <> "ftyp" : pos = payloadEnd
        End While
        If boxKind <> "ftyp" Then Return EmptyResult
        Dim majorBrandBuf(3) As Byte
        file.Seek(payloadStart, IO.SeekOrigin.Begin) : file.Read(majorBrandBuf, 0, 4)
        Dim majorBrand = Text.Encoding.ASCII.GetString(majorBrandBuf)
        '
        pos = start
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind <> "moov" : pos = payloadEnd : End While
        If boxKind <> "moov" Then Return EmptyResult
        Dim moovStart = payloadStart, moovEnd = payloadEnd
        '
        pos = moovStart : fend = moovEnd
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind <> "mvhd" : pos = payloadEnd : End While
        If boxKind <> "mvhd" Then Return EmptyResult
        Dim mvhdStart = payloadStart, mvhdEnd = payloadEnd
        '
        pos = moovStart : fend = moovEnd
        Dim cdayStart = 0L, cdayEnd = 0L
        Dim cnthStart = 0L, cnthEnd = 0L
        While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind <> "udta" : pos = payloadEnd : End While
        If boxKind = "udta" Then
            Dim udtaStart = payloadStart, udtaEnd = payloadEnd
            '
            pos = udtaStart : fend = udtaEnd
            While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind <> "©day" : pos = payloadEnd : End While
            If boxKind = "©day" Then cdayStart = payloadStart : cdayEnd = payloadEnd
            '
            pos = udtaStart : fend = udtaEnd
            While Mp4ReadNextBoxInfo(file, pos, fend, boxKind, payloadStart, payloadEnd) AndAlso boxKind <> "CNTH" : pos = payloadEnd : End While
            If boxKind = "CNTH" Then cnthStart = payloadStart : cnthEnd = payloadEnd
        End If

        ' The "mvhd" binary blob consists of 1byte (version, either 0 or 1), 3bytes (flags),
        ' and then either 4bytes (creation), 4bytes (modification)
        ' or 8bytes (creation), 8bytes (modification)
        ' If version=0 then it's the former, otherwise it's the later.
        ' In both cases "creation" and "modification" are big-endian number of seconds since 1st Jan 1904 UTC
        If mvhdEnd - mvhdStart < 20 Then Return EmptyResult
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
        If cdayStart <> 0 AndAlso cdayEnd - cdayStart > 4 Then
            file.Seek(cdayStart + 0, IO.SeekOrigin.Begin)
            cdayStringLen = file.Read2byte()
            If cdayStart + 4 + cdayStringLen <= cdayEnd Then
                file.Seek(cdayStart + 4, IO.SeekOrigin.Begin)
                Dim buf = New Byte(cdayStringLen - 1) {}
                file.Read(buf, 0, cdayStringLen)
                cdayString = System.Text.Encoding.ASCII.GetString(buf)
                Dim d As DateTimeOffset : If DateTimeOffset.TryParse(cdayString, d) Then dayTime = DateTimeKind.Local(d)
            End If
        End If

        ' The "CNTH" binary blob consists of 8bytes of unknown, followed by EXIF data
        Dim cnthTime As DateTimeKind? = Nothing, cnthLambda As UpdateTimeFunc = Nothing
        If cnthStart <> 0 AndAlso cnthEnd - cnthStart > 16 Then
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

        Return Tuple.Create(winnerTime, lambda)
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
        If size <> 1 Then
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

End Module
