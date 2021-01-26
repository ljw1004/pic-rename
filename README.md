# pic-rename.py

Usage: `pic-rename.py <files>`. This will renames photos to this kind of format:
* *Year - LocalTime(24hr) - Place.ext*
* 2021.01.25 - 21.05.49 - 24th Avenue East, Washington.heic
* 2019.12.20 - 12.34.12 - Black Sun, Volunteer Park, Seattle, Washington.jpg
* 2013.01.30 - 00.01.01 - The Butchart Gardens, Central Saanich, Vancouver Island, British Columbia, Canada.mp4
* 2018.05.06 - 13.15.03 - Champ de Mars, Eiffel Tower, Paris, Ile-de-France, France.mov
* 2011.04.30 - 15.30.01 - Angkor Wat, Siem Reap, Cambodia.png

The place-names are obtained by sending the photo's GPS coordinates to OpenStreetMaps servers; if the photo lacks GPS location then it just uses the original filename. The times are meant to be the local time at the place where the photo was taken, but some older phones and cameras only store the UTC time.