# FixCameraDate - specification

This is how you run FixCameraDate from the command-line:
```
FixCameraDate <filename> [/day+n] [/hour+n] [/minute-n] [/rename [<pattern>]]
```
The rest of this documentation explains the parts of that command-line, and what they do...

## `<Filename>`
This filename can include wildcards `*` and `?`. It can include subdirectories. Use quotation marks if your filenames include spaces. If you only include a `filename`, and none of `/rename` or `/hour` or `/minute`, then it will merely display the date and time that the photo/video was taken. (See "Concepts" section below for what exactly this means).
```
FixCameraDate *
FixCameraDate a.jpg
FixCameraDate *.jpg
FixCameraDate MAQ*.mp4
FixCameraDate "folder\*.*"
```

## `/day+n /hour+n /minute+n`
You can specify none, some or all of these options. They all accept positive numbers as well as negative numbers. They will update the time-taken of the file. Here are examples:
```
FixCameraDate a.jpg /hour+8
FixCameraDate * /hour-4 /minute+30
FixCameraDate "*.mp4" /minute+30
FixCameraDate "*.mov" /day+31
```

## `/rename <pattern>`
You can rename a file according to the date and time it was taken. (If you also specified `/hour` or `/minute`, then those adjustments are done first, and the rename is done second). Here are some example patterns.
```
FixCameraDate *.jpg /rename "%{datetime} - %{fn}"
FixCameraDate *.jpg /rename "%{date} - %{time} - %{fn}.jpg"
FixCameraDate *.mp4 /rename "%{fn} - %{month}-%{day}-%{year} %{hour}-%{minute}-%{second}"
FixCameraDate *.* /rename
```
* Each file will be renamed according to the pattern.
* The pattern can include things like `%{datetime}` or `%{fn}`, which will be filled in with the correct values taken from the file.
* If you just wrote `/rename` but without specifying a pattern, then it uses `"%{datetime} - %{fn}"`
* If the pattern doesn't include an extension (e.g. `.jpg` or `.mp4`) then when it renames the file it will just keep the same extension as it already had before.
* `%{fn}` is the original name of the file, minus its extension, with a special feature noted below.
* `%{datetime}` is the same as `%{date} - %{time}`
* `%{date}` is the same as `%{year}.%{month}.%{day}`
* `%{time}` is the same as `%{hour}.%{minute}.%{second}`
* `%{year}` is a four-digit year.
* `%{month}` is a two-digit month, with 01=January, 02=February, ...
* `%{day}` is the two-digit day of the month
* `%{hour}` is the two-digit hour, using the 24 hour clock. `%{minute}` and `%{second}` are obvious.

There's one useful feature to note. Look at the following example:
```
C:\photos\> dir
   10.53.00 - hello.jpg

C:\photos\> FixCameraDate *.jpg /hour+1 /rename "%{time} - %{fn}"
   11.53.00 - hello.jpg
```
In this case the original filename was `10.53.00 - hello.jpg`. But FixCameraDate saw that the original filename matched the pattern, and so `%{fn}` will stand for just `hello` rather than `10.53.00 - hello`.


# Concepts

*Local Time* - When I'm sorting my photo album, I want to know the _local time_ where the picture was taken. For instance, if the photo was taken on holiday in Italy at 9pm, then I want to sort the photo as 9pm Italian localtime, and not 8pm Coordinated Universal Time (UTC), and not 12noon Pacific Standard Time where I currently live.

FixCameraDate always works on local times, because they're the most useful.

*Metadata* - When a digital camera or cellphone takes a photo or video, it records the time in the file's _metadata_. How does it know what time? Well, every digital camera has a menu option to specify the current date, time and timezone. And cellphones get the current date and time from cellphone towers, and you have to specify timezone yourself.

But not all metadata is created equal...

*JPEG* - When any camera or cellphone takes a JPEG photo, it records the _local time_, but without saying which timezone it is. For instance, if I'd set the date+time to Italian time, and I took a photo at 8pm in Italy, then the JPEG metadata simply says "this photo was taken at 8pm local time, wherever local time happened to be."

*iPhone video* - When an iPhone takes a video, it records the _local time_ and also the _timezone_ where it was taken. That's great! That's as much as we can hope for.

*Android video* - When an Android phone takes a video, it records the Coordinated Universal Time (UTC) when the video was taken, and nothing more. That's what the video specification says to do, but it's not much use: you can't tell what timezone it was taken in, and so can't tell what the local time was. FixCameraDate will just assume that the photo was taken in the same timezone as your computer is currently running on. (Android also has a bug where it actually records the time 66 years in the past, but FixCameraDate is aware of this: whenever it sees a date prior to 1970, then it assumes it should add 66 years).

*Windows Phone video* - When a Windows Phone takes a video, it doesn't record anything at all in the metadata. But later (if you turned Skydrive syncing on) it will record the Coordinated Universal Time (UTC) when the video was uploaded to Skydrive. It also gives it a filename of the form "WP_20131216.mp4", which encodes the local date that the photo was taken on (but not the time). If the file was uploaded to Skydrive quickly, on the same date (as judged by the timezone where the computer is currently running) that the filename shows, then FixCameraDate will use that upload time. Otherwise, it will assume 12noon on the date specified by the filename.

*Sony video* - When a Sony Cybershot camera takes a video, it claims that it stores the Coordinated Universal Time (UTC) when the video was taken. But this is a lie. It actually just stores the local time when the video was taken. FixCameraDate knows this quirk, and if it detects that the video came from a Sony (because the Major_Brand of the MP4 file is "MSNV") then it treats the metadata time as a local time.

*Canon video* - When a Canon camera takes a video, it stores the Coordinated Universal Time (UTC). But by reverse-engineering the file format I discovered that it also stores a JPEG thumbnail that includes local-time! If FixCameraDate determines that the video came from a Canon camera, by the presence of this Canon thumbnail ("ftyp.moov.CNTH"), then it uses the local time.


# Design decisions

*Date corrections* - You might wonder why this program lets you specify `/day+1` but not `/month+1` or `/year+1`. The answer has to do with months and leap years. Imagine you had a photo taken on January 31st, and wanted to do `/month+1`. That would end up as February 31st, which doesn't exist. Imagine if you had a photo taken on January 29th on a leap year, and wanted to do `/year+1`. That would end up as February 29th on a non-leap-year, which also doesn't exist. The program doesn't let you alter months or years to avoid these conundrums. You're free to do `/day+365` if you want. Or should that be `/day+365 /hour+8` for 365.25 days? :)

*Time corrections* - If you specify `/day+n` or `/hour+n` or `/minute+n` then FixCameraDate updates whichever part of the file it got the local time from. If it got local time from metadata, then it updates metadata. If it got it from the time the file on disk was uploaded or created, then it updates that. If it got it from the filename, then it reports an error rather than trying to change the filename.

*Date format* - I picked the format `2013.12.25 - 18.30.00`. Why? For the date, I could have used ISO 8601 standard "2013-12-25", but the dashes are too often used as separators for other parts of the filename, so they didn't look good. As for the time, the standard format is "18:30:00" but colons aren't allowed in filenames.
 
