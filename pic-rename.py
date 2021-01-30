#!/usr/bin/python3

import sys
import datetime
import re
import os
import io
import urllib.request
import urllib.error
import urllib.parse
import threading
import hashlib
import traceback
import time
import socket
import xml.etree.ElementTree as ET
from typing import Tuple, Optional, List, IO, Dict
try:
    import pytz
except ImportError:
    pass


def parse_iso6709(s : str) -> Optional[Tuple[float,float]]:
    # https://en.wikipedia.org/wiki/ISO_6709
    # e.g. "+46.7888-124.0958+018.337/" which isn't strictly to spec, but is what iphoneXs produces
    pattern = re.compile(r'^([+-])(\d\d|\d\d\d\d|\d\d\d\d\d\d)(\.\d*)?([+-])(\d\d\d|\d\d\d\d\d|\d\d\d\d\d\d\d)(\.\d*)?[+-/]')
    match = pattern.match(s)
    if match is None:
        return None
    # Longitude
    (sign, val, frac) = (match.group(1), match.group(2), match.group(3))
    frac = '' if frac is None else frac
    if len(val) == 2:
        lat = float(sign+'1') * float(val + frac)
    elif len(val) == 4:
        lat = float(sign+'1') * (float(val[0:2]) + (float(val[2:] + frac)) / 60.0)
    else:
        lat = float(sign+'1') * (float(val[0:2]) + float(val[2:4]) / 60.0 + (float(val[4:] + frac)) / 3600.0)
    # Latitude
    (sign, val, frac) = (match.group(4), match.group(5), match.group(6))
    frac = '' if frac is None else frac
    if len(val) == 3:
        lon = float(sign+'1') * float(val + frac)
    elif len(val) == 5:
        lon = float(sign+'1') * (float(val[0:3]) + (float(val[3:] + frac)) / 60.0)
    else:
        lon = float(sign+'1') * (float(val[0:3]) + float(val[3:5]) / 60.0 + (float(val[5:] + frac)) / 3600.0)
    # done
    return (lat, lon)

def read_bytes(file: IO[bytes], pos:int, nbytes:int) -> bytes:
    file.seek(pos, io.SEEK_SET)
    return file.read(nbytes)

def read_string(file: IO[bytes], pos:int, nbytes: int, encoding: str = 'iso-8859-1') -> str:
    return read_bytes(file, pos, nbytes).decode(encoding)

def read_int(file: IO[bytes], pos:int, nbytes: int, byteorder : str = 'big', signed : bool = False) -> int:    
    return int.from_bytes(read_bytes(file, pos, nbytes), byteorder, signed = signed)

def read_date(file: IO[bytes], pos:int, nbytes:int) -> Optional[datetime.datetime]:
    # COMPATIBILITY-BUG: The spec says that these are expressed in seconds since 1904.
    # But my brother's Android phone picks them in seconds since 1970.
    # I'm going to guess that all dates before 1970 should be 66 years in the future
    # Note: I'm applying this correction *before* converting to date. That's because,
    # what with leap-years and stuff, it doesn't feel safe the other way around.
    TZERO_1904 = datetime.datetime(1904,1,1,0,0,0)
    TZERO_1970 = datetime.datetime(1970,1,1,0,0,0)
    TBUG_SECS = int((TZERO_1970 - TZERO_1904).total_seconds())
    seconds = read_int(file, pos, nbytes)
    if seconds == 0:
        return None
    seconds = seconds + TBUG_SECS if seconds < TBUG_SECS else seconds
    return TZERO_1904 + datetime.timedelta(seconds=seconds)

def exif_get_date_latlon_from_bom(file: IO[bytes], pos:int, end:int) -> Tuple[Optional[datetime.datetime], Optional[datetime.datetime], Optional[Tuple[float, float]], Optional[str]]:    
    latNS : Optional[str] = None
    latEW : Optional[str] = None
    latNum : Optional[float] = None
    lonNum : Optional[float] = None
    timeLastModified : Optional[str] = None
    timeOriginal : Optional[str] = None
    timeDigitized : Optional[str] = None
    offsetLastModified : Optional[str] = None
    offsetOriginal : Optional[str] = None
    offsetDigitized : Optional[str] = None

    exif_bom = read_bytes(file, pos, 4)
    if exif_bom != b'MM\x00*' and exif_bom != b'II*\x00':
        return (None, None, None, f'Exif unrecognized BOM {str(exif_bom)}')
    byteorder = 'little' if exif_bom == b'II*\x00' else 'big'
    ipos = read_int(file, pos+4, 4, byteorder=byteorder)
    if ipos + 12 >= end:
        return (None, None, None, f'Exif marker size wrong')
    # Format of EXIF is a chain of IFDs. Each consists of a number of tagged entries.
    # One of the tagged entries may be "SubIFDpos = &H..." which gives the address of the
    # next IFD in the chain; if this entry is absent or 0, then we're on the last IFD.
    # Another tagged entry may be "GPSInfo = &H..." which gives the address of the GPS IFD
    subifdpos = 0
    gpsifdpos = 0
    while True:
        ibuf = pos + ipos
        nentries = read_int(file, ibuf+0, 2, byteorder=byteorder)
        if 10 + ipos + 2 + nentries*12 + 4 > end:
            continue # error in ifd header
        ipos = read_int(file, ibuf+2+nentries*12, 4, byteorder=byteorder)
        for i in range(nentries):
            ebuf = ibuf + 2 + i * 12
            tag = read_int(file, ebuf+0, 2, byteorder=byteorder)
            format = read_int(file, ebuf+2, 2, byteorder=byteorder)
            ncomps = read_int(file, ebuf+4, 4, byteorder=byteorder)
            data = read_int(file, ebuf+8, 4, byteorder=byteorder)
            if tag == 0x8769 and format == 4:
                subifdpos = data
            elif tag == 0x8825 and format == 4:
                gpsifdpos = data
            elif (tag == 1 or tag == 3) and format == 2 and ncomps == 2:
                if tag == 1:
                    latNS = chr(data >> 24)
                else:
                    lonEW = chr(data >> 24)
            elif (tag == 2 or tag == 4) and format == 5 and ncomps == 3 and 10 + data + ncomps <= end:
                ddpos = pos + data
                degTop = float(read_int(file, ddpos+0, 4, byteorder=byteorder))
                degBot = float(read_int(file, ddpos+4, 4, byteorder=byteorder))
                minTop = float(read_int(file, ddpos+8, 4, byteorder=byteorder))
                minBot = float(read_int(file, ddpos+12, 4, byteorder=byteorder))
                secTop = float(read_int(file, ddpos+16, 4, byteorder=byteorder))
                secBot = float(read_int(file, ddpos+20, 4, byteorder=byteorder))
                deg = degTop / degBot + minTop / minBot / 60.0 + secTop / secBot / 3600.0
                if tag == 2:
                    latNum = deg
                else:
                    lonNum = deg
            elif (tag == 0x132 or tag == 0x9003 or tag == 0x9004) and format == 2 and ncomps == 20 and 10 + data + ncomps <= end:
                s = read_string(file, pos+data, ncomps-1, 'ascii')
                if tag == 0x132:
                    timeLastModified = s
                elif tag == 0x9003:
                    timeOriginal = s
                elif tag == 0x9004:
                    timeDigitized = s
            elif (tag == 0x9010 or tag == 0x9011 or tag == 0x9012) and format == 2 and 10 + data + ncomps <= end:
                s = read_string(file, pos+data, ncomps-1, 'ascii')
                if tag == 0x9010:
                    offsetLastModified = s
                elif tag == 0x9011:
                    offsetOriginal = s
                elif tag == 0x9022:
                    offsetDigitized = s
            else:
                pass
        if ipos == 0:
            (ipos, subifdpos) = (subifdpos, 0)
            if ipos == 0:
                (ipos, gpsifdpos) = (gpsifdpos, 0)
                if ipos == 0:
                    break  #  indicates the last IFD in this marker

    date : Optional[datetime.datetime] = None
    for (date_str, offset_str) in [(timeLastModified, offsetLastModified), (timeOriginal, offsetOriginal), (timeDigitized, offsetDigitized)]:
        if date_str is None:
            continue
        try:
            if offset_str is None:
                date_val = datetime.datetime.strptime(date_str, "%Y:%m:%d %H:%M:%S")
            else:
                date_val = datetime.datetime.strptime(date_str + offset_str, "%Y:%m:%d %H:%M:%S%z")
            if date is None or date_val < date:
                date = date_val
        except:
            continue
    if date is None:
        return (None, None, None, 'exif lacks times')

    latlon : Optional[Tuple[float,float]] = None
    if latNum is not None and latNS is not None and lonNum is not None and lonEW is not None:
        lat = latNum * (1 if latNS == 'N' else -1)
        lon = lonNum * (1 if latEW == 'E' else -1)
        latlon = (lat, lon)
    return (date, None, latlon, None)


def get_exif_date_latlon(file: IO[bytes], pos:int, end:int) -> Tuple[Optional[datetime.datetime], Optional[datetime.datetime], Optional[Tuple[float, float]], Optional[str]]:
    # https://www.wikidata.org/wiki/Q26381818
    # http://cipa.jp/std/documents/e/DC-008-2012_E_C.pdf
    h1 = read_int(file, pos, 2)  # expected to be 0xFFD8, "SOI" StartOfImage
    pos = pos + 2
    while True: # iterate through EXIF markers
        mbuf = pos
        if mbuf+4 > end:
            return (None, None, None, f'did not find TIFF Exif block')
        marker = read_int(file, mbuf+0, 2)
        msize = read_int(file, mbuf+2, 2)
        if mbuf + msize > end:
            return (None, None, None, f'TIFF block size mismatch')
        pos += 2+msize
        if marker == 0xFFDA: # image data follows this marker; we can stop our iteration
            return (None, None, None, f'did not find TIFF Exif block before image data')
        if marker != 0xFFE1: # we'll skip non-EXIF markers
            continue
        if msize < 14:
            continue
        if read_bytes(file, mbuf+4, 4) != b'Exif':
            continue
        if read_int(file, mbuf+8, 2) != 0: # and with this header
            continue
        return exif_get_date_latlon_from_bom(file, mbuf+10, mbuf+msize)

def mp4_read_next_box(file: IO[bytes], pos:int, end:int) -> Tuple[Optional[bytes], int, int]:
    eof = (None, 0, 0)
    if pos+8 > end:
        return eof
    size = read_int(file, pos, 4)
    kind = read_bytes(file, pos+4, 4)
    if size != 1:
        return (kind, pos+8, pos+size) if pos+size<=end and size != 0 else eof
    elif size == 1 and pos + 16 < end:
        size = read_int(file, pos+8, 8)
        return (kind, pos+16, pos+size) if pos+size<=end and size != 0 else eof
    else:
        return eof

def mp4_find_box(file: IO[bytes], kind: bytes, pos:int, end:int) -> Tuple[Optional[bytes], int, int]:
    while True:        
        (box_kind, box_start, box_end) = mp4_read_next_box(file, pos, end)
        if box_kind is None:
            return (None, 0, 0)
        elif box_kind == kind:
            return (box_kind, box_start, box_end)
        else:
            pos = box_end

def debug_print_mp4_hierarchy(file: IO[bytes], pos:int, end:int, prefix:str = ''):
    while True:
        (box_kind, box_start, box_end) = mp4_read_next_box(file, pos, end)
        if box_kind is None:
            return
        len = max(min(box_end - box_start, 24),0)
        print(f'{prefix}{str(box_kind)}:{box_start}-{box_end}:{str(read_bytes(file,box_start,len))}{"..." if len < box_end-box_start else ""}')
        # Does the box have child boxes in it? are the children offset from the start of the box?
        if box_kind == b'mdat' or box_kind == b'ftyp' or box_kind == b'infe' or box_kind == b'iloc':
            pass
        elif box_kind == b'meta' or box_kind == b'iref':
            version=read_int(file, box_start+0, 4)
            debug_print_mp4_hierarchy(file, box_start+4, box_end, prefix+'  ')
        elif box_kind == b'iinf':
            version=read_int(file, box_start+0, 4)
            item_count=read_int(file, box_start+4, 2 if version == 0 else 4)
            debug_print_mp4_hierarchy(file, box_start + (6 if version == 0 else 8), box_end, prefix+'  ')
        else:
            # I can't be bothered to hard-code every single other box type, so here let's
            # just blindly hope for the best... This will be wrong on many box kinds!
            debug_print_mp4_hierarchy(file, box_start, box_end, prefix+'  ')
        pos = box_end

def get_mp4_date_latlon(file: IO[bytes], pos:int, end:int) -> Tuple[Optional[datetime.datetime], Optional[datetime.datetime], Optional[Tuple[float, float]], Optional[str]]:
    # official spec: https://mpeg.chiariglione.org/standards/mpeg-4/iso-base-media-file-format/text-isoiec-14496-12-5th-edition
    # readable spec: https://clanmills.com/exiv2/book/
    # Worked example: https://leo-van-stee.github.io/
    # The file is made up of a sequence of boxes, with a standard way to find size and FourCC "kind" of each.
    # Some box kinds contain a kind-specific blob of binary data. Other box kinds contain a sequence
    # of sub-boxes. You need to look up the specs for each kind to know whether it has a blob or sub-boxes.
    # We look for a top-level box of kind "moov", which contains sub-boxes, and then we look for its sub-box
    # of kind "mvhd", which contains a binary blob. This is where Creation/ModificationTime are stored.
    latlon : Optional[Tuple[float, float]] = None

    # HEIF files have meta.iinf which describes all their items
    # Here are example HIEF images: https://github.com/nokiatech/heif/tree/gh-pages/content
    # implementation: https://fossies.org/linux/Image-ExifTool/lib/Image/ExifTool/QuickTime.pm
    (meta_kind, meta, meta_end) = mp4_find_box(file, b'meta', pos, end)
    (iinf_kind, iinf, iinf_end) = mp4_find_box(file, b'iinf', meta+4, meta_end)
    item_ID_for_exif : Optional[int] = None
    if iinf_end - iinf >= 8:
        iinf_version = read_int(file, iinf+0, 4)
        iinf_item_count = read_int(file, iinf+4, 2 if iinf_version == 0 else 4)
        iinf_pos = iinf + (6 if iinf_version == 0 else 8)
        while True:
            (infe_kind, infe, infe_end) = mp4_read_next_box(file, iinf_pos, iinf_end)
            iinf_pos = infe_end
            if infe_kind != b'infe' or infe + 12 > infe_end:
                break
            infe_version = read_int(file, infe+0, 4) >> 24
            if infe_version != 2:
                break
            infe_item_ID = read_int(file, infe+4, 2)
            infe_item_type = read_bytes(file, infe+8, 4)
            if infe_item_type == b'Exif':
                item_ID_for_exif = infe_item_ID
    (iloc_kind, iloc, iloc_end) = mp4_find_box(file, b'iloc', meta+4, meta_end)
    if iloc_end - iloc >= 8:
        iloc_version = read_int(file, iloc, 4) >> 24
        iloc_sizes = read_int(file, iloc+4, 2)
        iloc_offset_size = (iloc_sizes >> 12) & 0x0F
        iloc_length_size = (iloc_sizes >> 8) & 0x0F
        iloc_base_offset_size = (iloc_sizes >> 4) & 0x0F
        iloc_index_size = (iloc_sizes >> 0) & 0x0F
        iloc_items_count = read_int(file, iloc+6, 2 if iloc_version<2 else 4)
        iloc_pos = iloc + (8 if iloc_version<2 else 10)
        iloc_i = 0
        while iloc_version <= 2 and iloc_pos + 16 <= iloc_end and iloc_i < iloc_items_count:
            item_ID = read_int(file, iloc_pos+0, 2 if iloc_version < 2 else 4)
            construction_method = 0 if iloc_version == 0 else read_int(file, iloc_pos + iloc_version*2, 2)
            data_reference_index = read_int(file, iloc_pos + iloc_version*2 + 2, 2)
            base_offset = 0 if iloc_base_offset_size == 0 else read_int(file, iloc_pos + iloc_version*2 + 4, iloc_base_offset_size)
            extent_count = read_int(file, iloc_pos + iloc_version*2 + 4 + iloc_base_offset_size, 2)
            extent_size = iloc_offset_size + iloc_length_size + (0 if iloc_version == 0 else iloc_index_size)
            extent = iloc_pos + iloc_version*2 + 4 + iloc_base_offset_size + 2 
            iloc_pos = extent + extent_count * extent_size
            iloc_i += 1
            if item_ID != item_ID_for_exif:
                continue # we're only interested in exif
            if construction_method != 0 or data_reference_index != 0 or extent_count != 1 or base_offset != 0:
                continue # these other methods haven't yet been implemented
            extent_offset = read_int(file, extent + (0 if iloc_version == 0 else iloc_index_size), iloc_offset_size)
            extent_length = read_int(file, extent + iloc_offset_size + (0 if iloc_version == 0 else iloc_index_size), iloc_length_size)
            if extent_offset + extent_length > end:
                continue
            tag = read_bytes(file, extent_offset+4, 4)
            if tag != b'Exif':
                continue
            return exif_get_date_latlon_from_bom(file, extent_offset+10, extent_offset+extent_length)

    (moov_kind, moov, moov_end) = mp4_find_box(file, b'moov', pos, end)

    # The optional "moov.meta.ilst" is what iphoneXs uses
    # https://developer.apple.com/library/archive/documentation/QuickTime/QTFF/Metadata/Metadata.html
    (meta_kind, meta, meta_end) = mp4_find_box(file, b'meta', moov, moov_end)
    (keys_kind, keys, keys_end) = mp4_find_box(file, b'keys', meta, meta_end)
    (ilst_kind, ilst, ilst_end) = mp4_find_box(file, b'ilst', meta, meta_end)
    # assemble all the keys
    allkeys : List[Tuple[bytes, bytes]] = [(b'',b'')] # index 0 is never used
    if keys + 8 <= keys_end:
        key_count = read_int(file, keys+4, 4)
        kpos = keys+8
        for ikey in range(0,key_count):
            if kpos + 8 > keys_end:
                break
            key_size = read_int(file, kpos, 4)
            if kpos + key_size > keys_end:
                break
            key_namespace = read_bytes(file, kpos+4, 4)
            key_value = read_bytes(file, kpos+8, key_size-8)
            allkeys.append((key_namespace, key_value))
            kpos = kpos + key_size
    # walk through the ilst sub-boxes, looking for location+date
    if ilst + 16 <= ilst_end:
        ilst_pos = ilst
        date: Optional[datetime.datetime] = None
        while True:
            (item_kind, item_start, item_end) = mp4_read_next_box(file, ilst_pos, ilst_end)
            if item_kind is None or item_start + 16 > item_end:
                break
            ilst_pos = item_end
            ikey = int.from_bytes(item_kind, 'big')
            if ikey == 0 or ikey >= len(allkeys):
                break
            (namespace, key) = allkeys[ikey]
            item_type = read_int(file, item_start+8, 4)
            item_locale = read_int(file, item_start+12, 4)
            item_value = read_string(file, item_start+16, item_end - item_start - 16, 'utf8') if item_type == 1 else None
            if key == b'com.apple.quicktime.location.ISO6709' and item_value is not None:
                latlon = parse_iso6709(item_value)
            if key == b'com.apple.quicktime.creationdate' and item_value is not None:
                try:
                    date = datetime.datetime.strptime(item_value, r'%Y-%m-%dT%H:%M:%S%z') # iphoneXs uses example "2021-01-16T20:29:24-0800"
                except:
                    try:
                        date = datetime.datetime.strptime(item_value, r'%-m/%-d/%Y') # apple docs give example "4/21/2012"
                    except:
                        pass
        if date is not None:
            return (date, None, latlon, None)    

    # The optional "moov.udta.CNTH" binary blob consists of 8bytes of unknown, followed by EXIF data
    # If present, we'll use that since it provides GPS as well as time.
    (udta_kind, udta, udta_end) = mp4_find_box(file, b'udta', moov, moov_end)
    (cnth_kind, cnth, cnth_end) = mp4_find_box(file, b'CNTH', udta, udta_end)    
    if cnth + 16 <= cnth_end:
        return get_exif_date_latlon(file, cnth+8, cnth_end)
    
    # The optional "moov.udta.©xyz" blob consists of len (2bytes), lang (2bytes), iso6709 gps (size bytes)
    (cxyz_kind, cxyz, cxyz_end) = mp4_find_box(file, b'\xA9xyz', udta, udta_end)
    if cxyz + 4 <= cxyz_end:
        cxyz_len = read_int(file, cxyz+0, 2)
        if cxyz + cxyz_len <= cxyz_end:
            cxyz_str = read_string(file, cxyz+4, cxyz_len, 'utf-8')
            latlon = parse_iso6709(cxyz_str)

    # The "mvhd" binary blob consists of 1byte (version, either 0 or 1), 3bytes (flags),
    # and then either (if version=0) 4bytes (creation), 4bytes (modification)
    # or (if version=1) 8bytes (creation), 8bytes (modification)
    # In both cases "creation" and "modification" are big-endian number of seconds since 1st Jan 1904 UTC
    (mvhd_kind, mvhd, mvhd_end) = mp4_find_box(file, b'mvhd', moov, moov_end)
    if mvhd + 20 <= mvhd_end:
        mvhd_version = read_int(file, mvhd, 1)
        mvhd_date_bytes = 4 if mvhd_version == 0 else 8
        creation_time_utc = read_date(file, mvhd+4, mvhd_date_bytes)
        # COMPATIBILITY-BUG: The spec says that these times are in UTC.
        # However, my Sony Cybershot merely gives them in unspecified time (i.e. local time but without specifying the timezone)
        # Indeed its UI doesn't even let you say what the current UTC time is.
        # I also noticed that my Sony Cybershot gives MajorBrand="MSNV", which isn't used by my iPhone or Canon or WP8.
        # I'm going to guess that all "MSNV" files come from Sony, and all of them have the bug.
        (ftyp_kind, ftyp, ftyp_end) = mp4_find_box(file, b'ftyp', pos, end)
        major_brand = read_bytes(file, ftyp, 4)  # e.g. "qt" for iphone, "MSNV" for Sony
        if creation_time_utc is None:
            return (None, None, latlon, 'mp4 metadata is missing date')
        elif major_brand == b'MSNV':
            return (creation_time_utc, None, latlon, None)
        else:
            return (None, creation_time_utc, latlon, None)

    # There are other optional blocks that may help, e.g. b'\xA9day' contains a local
    # time and a UTC offset on some cameras. But they're rare enough that I won't bother.
    return (None, None, None, 'No metadata atoms')

def get_png_date_latlon(file: IO[bytes], pos:int, end:int) -> Tuple[Optional[datetime.datetime], Optional[datetime.datetime], Optional[Tuple[float, float]], Optional[str]]:
    # http://www.libpng.org/pub/png/spec/1.2/PNG-Structure.html#PNG-file-signature
    # http://ftp-osl.osuosl.org/pub/libpng/documents/pngext-1.5.0.html#C.eXIf
    # A series of chunks.
    date : Optional[datetime.datetime] = None
    pos = 8
    while True:
        if pos + 12 > end:
            break
        length = read_int(file,pos+0,4,'big')
        type = read_bytes(file, pos+4, 4)
        if pos + 12 + length > end:
            break
        crc = read_int(file, pos+8+length, 4)
        chunk = pos
        pos = pos + 12 + length
        if type == b'eXIf':
            return exif_get_date_latlon_from_bom(file, chunk+8, chunk+8+length)
        if type == b'tEXt':
            # key, null, value, all in latin1
            bytes = read_bytes(file, chunk+8, length)
            null = bytes.find(b'\x00')
            if null == -1:
                continue
            key = bytes[:null].decode('latin1')
            value = bytes[null+1:].decode('latin1')
            if key != 'date:create' and key != 'date:modify':
                continue
            try:
                value_date = datetime.datetime.strptime(value, r'%Y-%m-%dT%H:%M:%S%z') # suggested to have this form but might be freeform
                if date is None or value_date < date:
                    date = value_date
            except:
                continue
    if date is None:
        return (None, None, None, 'No eXIf or date found in PNG')
    else:
        return (date, None, None, None)

def get_date_latlon(src : str) -> Tuple[Optional[datetime.datetime], Optional[datetime.datetime], Optional[Tuple[float, float]], Optional[str]]:
    # some file format pointers: http://nokiatech.github.io/heif/technical.html
    # heic: http://cheeky4n6monkey.blogspot.com/2017/10/monkey-takes-heic.html
    try:
        with open(src,"rb") as file:
            file.seek(0, io.SEEK_END)
            fend = file.tell()
            if fend < 8:
                return (None,None,None,"file too small")
            header = read_bytes(file,0,8)
            if header[0:2] == b'\xFF\xD8': # jpeg header
                return get_exif_date_latlon(file, 0, fend)
            elif header[4:8] == b'ftyp': # mp4 header
                return get_mp4_date_latlon(file, 0, fend)
            elif header[0:8] == b'\x89PNG\x0d\x0a\x1a\x0a':
                return get_png_date_latlon(file, 0, fend)
            else:
                return (None,None,None,f'unrecognized header {str(header)}')
    except Exception as e:
        return (None,None, None, f'unable to open {e}')

def urlopen_and_retry_on_busy(url : str) -> bytes:
    try:
        os.makedirs('/tmp/pic-rename')
    except OSError:
        pass
    cache = f'/tmp/pic-rename/cache_{hashlib.md5(url.encode()).hexdigest()}'
    try:
        with open(cache,"rb") as file:
            return file.read()
    except:
        pass
    while True:
        reason : Optional[str] = None
        try:
            with urllib.request.urlopen(url) as response:
                content = response.read()
                with open(cache,"wb") as file:
                    file.write(content)
                return content
        except urllib.error.HTTPError as e:
            if e.code == 429 or e.code == 504: # 429=too many requests, 504=gateway timeout
                reason = f'{e.code} {str(e.reason)}'
            elif isinstance(e.reason, socket.timeout):
                reason = f'HTTPError socket.timeout {e.reason} - {e}'
            else:
                raise
        except urllib.error.URLError as e:
            if isinstance(e.reason, socket.timeout):
                reason = f'URLError socket.timeout {e.reason} - {e}'
            else:
                raise
        except socket.timeout as e:
            reason = f'socket.timeout {e}'
        except:
            raise
        netloc = urllib.parse.urlsplit(url).netloc # e.g. nominatim.openstreetmap.org
        print(f'*** {netloc} {reason}; will retry', file=sys.stderr)
        time.sleep(5)


def get_place_tz_from_latlon(latlon : Tuple[float, float]) -> Tuple[str,Optional[datetime.tzinfo]]:
    (lat, lon) = latlon
    parts : List[Tuple[str,str]] = []

    # Nominatim has pretty good breakdowns
    parts1 : List[Tuple[str,str]] = []
    url1 = f'http://nominatim.openstreetmap.org/reverse?accept-language=en&format=xml&lat={lat:0.7f}&lon={lon:0.7f}&zoom=18'
    raw1 = urlopen_and_retry_on_busy(url1)
    xml1 = ET.fromstring(raw1) # e.g. <reversegeocode><result>Here</result><addressparts><road>Here</road><country>There</country></addressparts></reversegeocode>
    summary1 = xml1.findtext(".//result")
    result1 = xml1.find(".//result")
    addressparts1 = xml1.find(".//addressparts")
    for apart in (list(addressparts1) if addressparts1 is not None else []):
        if apart.text is not None:
            atag = 'tourism' if apart.tag in ['leisure', 'aeroway', 'historic'] else 'amenity' if apart.tag in ['building', 'shop', 'retail'] else 'suburb' if apart.tag in ['hamlet'] else apart.tag
            parts1.append((atag, apart.text))
    # I disagree with the way London is stored...
    if ('state_district', 'Greater London') in parts1:
        parts1.append(('city','London'))    

    # Overpass provides some additional tags that are sometimes missing from Nominatim.
    parts2 : List[Tuple[str,str]] = []
    url2 = f'http://overpass-api.de/api/interpreter?data=is_in({lat:0.7f},{lon:0.7f});out;'
    raw2 = urlopen_and_retry_on_busy(url2)
    xml2 = ET.fromstring(raw2)
    tz2 : Optional[datetime.tzinfo] = None
    for area in xml2.iterfind(".//area"):  # e.g. <area><tag k="admin_level" v="1"/><tag k="name" v='Creedon"/></area>
        tags : Dict[str,str] = { tag.get('k','_') : tag.get('v','_') for tag in area.iterfind(".//tag") if tag.get('k') is not None and tag.get('v') is not None} # {type:boundary, boundary:administrative, admin_level:1, name:fred}
        name = tags.get('name:en') if tags.get('name:en') is not None else tags.get('name')
        area_type = tags.get('type')
        boundary = tags.get('boundary')
        admin_level = int(tags['admin_level']) if 'admin_level' in tags and tags['admin_level'].isdigit() else None
        if 'timezone' in tags and tz2 is None and 'pytz' in sys.modules:
            try:
                tz2 = pytz.timezone(tags['timezone'])
            except pytz.UnknownTimeZoneError:
                pass
        if name is None:
            pass
        elif area_type == 'boundary' and boundary == 'administrative' and admin_level is not None:
            parts2.append((f'{admin_level}', name))
        elif tags.get('building') is not None or tags.get('amenity') is not None:
            parts2.append(('amenity', name))
        elif tags.get('leisure') is not None or tags.get('tourism') is not None:
            parts2.append(('tourism', name))
        elif area_type == 'site' or (area_type == 'multipolygon' and name != 'Great Britain'):
            parts2.append((f'multipolygon', name))

    # Assemble all this into a name. Our challenge is to use heuristics that capture only the
    # key human-centric parts, and omit redundant information
    # https://wiki.openstreetmap.org/wiki/Tag:boundary%3Dadministrative#11_admin_level_values_for_specific_countries
    parts = [*parts1, *parts2]
    partsdic = { key:val for (key,val) in parts}
    amenities : List[str] = list(name for (key,name) in parts if key == 'amenity')
    house_number = (partsdic['house_number']+' ') if 'house_number' in partsdic else ''
    road : Optional[str] = (house_number + partsdic['road']) if 'road' in partsdic else None
    tourism : List[str] = list(name for (key,name) in parts if key == 'tourism' and ('amenity', name) not in parts)
    suburb_candidates : List[Optional[str]] = [partsdic.get('neighbourhood'), partsdic.get('neighborhood'), partsdic.get('suburb'), partsdic.get('10'), partsdic.get('9')]
    suburb : Optional[str] = next((suburb for suburb in suburb_candidates if suburb is not None), None)
    city_candidates : List[Optional[str]] = [partsdic.get('town'), partsdic.get('city'), partsdic.get('8'), partsdic.get('7'), partsdic.get('county'), partsdic.get('6')]
    city : Optional[str] = next((city for city in city_candidates if city is not None), None)
    multipolygons : List[str] = list(name for (key,name) in parts if key == 'multipolygon')
    state_candidates : List[Optional[str]] = [partsdic.get('state'), partsdic.get('4'), partsdic.get('5')]
    state : Optional[str] = next((state for state in state_candidates if state is not None), None)    
    country_candidates : List[Optional[str]] = [partsdic.get('country'), partsdic.get('2')]
    country : Optional[str] = next((country for country in country_candidates if country is not None), None)

    # Amenities, tourism and particularly multipolygons are always the most interesting parts -
    # they represent human-centric boundaries that are so important that someone went to the trouble
    # of manually marking them out and entering them into the databases.
    keyparts : List[str] = []
    preferSuburbOverRoad : Optional[bool] = None if len(tourism)>0 else True if len(amenities)>0 else False
    keyparts.extend(amenities)
    if road is not None and (preferSuburbOverRoad == False or (preferSuburbOverRoad == True and suburb is None)):
        keyparts.append(road)
    keyparts.extend(tourism)
    if suburb is not None and (preferSuburbOverRoad == True or (preferSuburbOverRoad == False and road is None)):
        keyparts.append(suburb)
    if city is not None:
        keyparts.append(city)
    keyparts.extend(multipolygons)

    # To avoid repetition, remove all words that appear earlier too
    preceding : List[str] = []
    unique = []
    strip_parentheses = {ord(forbidden):'' for forbidden in '()[]'}
    for part in keyparts:
        words = [word for word in part.split() if word.translate(strip_parentheses) not in preceding]
        words_for_preceding = [word.translate(strip_parentheses) for word in words]
        preceding.extend(words_for_preceding)
        if len(words) > 0:
            unique.append(" ".join(words))

    if state is not None and state not in unique:
        unique.append(state)
    if country is not None and (state is None or (country != 'United States' and country != 'United Kingdom')) and country not in unique:
        unique.append(country)

    # Sanitize
    place = ", ".join(unique)
    place = place.translate({ord(forbidden):' ' for forbidden in '\\/?%*?:|'}).replace('  ',' ')
    place = place[:120]
    return (place, tz2)

def test_iso6709():
    assert(parse_iso6709("+46.7888-124.0958+018.337/") == (46.7888,-124.0958))
    assert(parse_iso6709("+00-025/") == (0,-25))
    assert(parse_iso6709("+46+002/") == (46,2))
    assert(parse_iso6709("+48.8577+002.295/") == (48.8577,2.295))
    assert(parse_iso6709("+27.5916+086.5640+8850CRSWGS_84/") == (27.5916,86.5640))
    assert(parse_iso6709("+90+000/") == (90,0))
    assert(parse_iso6709("+00-160/") == (0,-160))
    assert(parse_iso6709("-90+000+2800CRSWGS_84/") == (-90,0))
    assert(parse_iso6709("+38-097/") == (38,-97))
    assert(parse_iso6709("+40.75-074.00/") == (40.75,-74))
    assert(parse_iso6709("+40.6894-074.0447/") == (40.6894,-74.0447))
    assert(parse_iso6709("+1234.56-09854.321/") == (12.576,-98.90535))
    assert(parse_iso6709("+123456.7-0985432.1/") == (12.582416666666667,-98.90891666666667))
    assert(parse_iso6709("+27.5916+086.5640+8850/") == (27.5916,86.564))
    assert(parse_iso6709("-90+000+2800/") == (-90,0))
    assert(parse_iso6709("+40.75-074.00/") == (40.75,-74))
    assert(parse_iso6709("+352139+1384339+3776/") == (35.36083333333333,138.7275))
    assert(parse_iso6709("+35.658632+139.745411/") == (35.658632,139.745411))

def test_metadata():
    dir = os.path.abspath(os.path.join(os.path.dirname(__file__),'test'))
    assert(get_date_latlon(os.path.join(dir,'eg-android - 2013.11.23 - 12.49 PST.mp4')) == (None, datetime.datetime(2013,11,23,20,49,51), None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-android - 2013.12.28 - 15.48 PST.jpg')) == (datetime.datetime(2013,12,28,15,48,42), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-android - 2013.12.28 - 15.48 PST.mp4')) == (None, datetime.datetime(2013,12,28,23,48,57), None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-canon-ixus - 2013.12.15 - 07.30 PST.jpg')) == (datetime.datetime(2013, 12, 15, 7, 31, 41), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-canon-ixus - 2013.12.15 - 07.30 PST.mov')) == (datetime.datetime(2013, 12, 15, 7, 30, 58), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-canon-powershot - 2013.12.28 - 15.51 PST.jpg')) == (datetime.datetime(2013, 12, 28, 15, 51, 11), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-canon-powershot - 2013.12.28 - 15.51 PST.mov')) == (datetime.datetime(2013, 12, 28, 15, 51, 27), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-depstech - 2020.01.20 - 20.40 PST.jpg')) == (None, None, None, 'exif lacks times'))
    assert(get_date_latlon(os.path.join(dir,'eg-iphone4s - 2013.12.28 - 15.49 PST.jpg')) == (datetime.datetime(2013, 12, 28, 15, 50, 10), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-iphone4s - 2013.12.28 - 15.49 PST.mov')) == (datetime.datetime(2013, 12, 28, 15, 50, 22, tzinfo=datetime.timezone(datetime.timedelta(days=-1, seconds=57600))), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-iphone5 - 2013.12.09 - 15.21 PST.mov')) == (datetime.datetime(2013, 12, 9, 15, 21, 37, tzinfo=datetime.timezone(datetime.timedelta(days=-1, seconds=57600))), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-iphone5 - 2013.12.10 - 15.40 PST.jpg')) == (datetime.datetime(2013, 12, 10, 15, 39, 54), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-iphone6-gps.jpg')) == (datetime.datetime(2016,2,18,21,10,48), None, (47.63614722222222,-122.30151388888889), None))
    assert(get_date_latlon(os.path.join(dir,'eg-iphonexs - 2021.01.16 - 07.00 PST.png')) == (datetime.datetime(2021, 1, 16, 7, 0, 51), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-iphonexs - 2021.01.17 - 12.18 PST.heic')) == (datetime.datetime(2021, 1, 17, 12, 18, 23, tzinfo=datetime.timezone(datetime.timedelta(days=-1, seconds=57600))), None, (46.79380555555555, -124.10501944444444), None))
    assert(get_date_latlon(os.path.join(dir,'eg-iphonexs - 2021.01.17 - 20.29 PST.mov')) == (datetime.datetime(2021, 1, 16, 20, 29, 24, tzinfo=datetime.timezone(datetime.timedelta(days=-1, seconds=57600))), None, (46.7888, -124.0958), None))
    assert(get_date_latlon(os.path.join(dir,'eg-iphonexs-memory - 2021.01.25 - 19.15 PST.mov')) == (None, datetime.datetime(2021, 1, 26, 3, 15, 25), None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-notapic.txt')) == (None, None, None, "unrecognized header b'This is '"))
    assert(get_date_latlon(os.path.join(dir,'eg-screenshot.png')) == (None, None, None, 'No eXIf or date found in PNG'))
    assert(get_date_latlon(os.path.join(dir,'eg-sony-cybershot - 2013.12.15 - 07.30 PST.jpg')) == (datetime.datetime(2013, 12, 15, 7, 32, 37), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-sony-cybershot - 2013.12.15 - 07.30 PST.mp4')) == (datetime.datetime(2013, 12, 15, 7, 31, 51), None, None, None))
    assert(get_date_latlon(os.path.join(dir,'eg-wm10-gps.jpg')) == (datetime.datetime(2016, 2, 15, 22, 20, 58), None, (47.63564167544167, -122.30185414664444), None))
    assert(get_date_latlon(os.path.join(dir,'eg-wm10.mp4')) == (None, datetime.datetime(2016, 2, 25, 4, 27, 35), (47.6361, -122.3013), None))
    assert(get_date_latlon(os.path.join(dir,'eg-wp8 - 2013.12.15 - 07.33 PST.jpg')) == (datetime.datetime(2013,12,15,7,32,50), None, (47.63610444444444, -122.30139333333334), None))
    assert(get_date_latlon(os.path.join(dir,'eg-wp8 - 2013.12.15 - 07.33 PST.mp4')) == (None, None, None, 'mp4 metadata is missing date'))

def test_place():
    # United States
    assert(get_place_tz_from_latlon((47.637922, -122.301557)) == ('24th Avenue East, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.629612, -122.315119)) == ('Black Sun, Volunteer Park, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.639483, -122.29801)) == ('Pinetum, Washington Park Arboretum, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.65076, -122.302043)) == ('Husky Football Stadium, University of Washington, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.668719, -122.38296)) == ('Washington Federal Bank, Ballard, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.681006, -122.407513)) == ('Shilshole Bay Marina, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.620415, -122.349463)) == ('Space Needle, Seattle Center, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.609839, -122.342981)) == ('Pike Place Market, Seattle, street, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.65464, -122.30843)) == ('University of Washington, District, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.64529, -122.13064)) == ('Microsoft Building 25, Northeast 39th Street, Redmond, East Campus, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((48.67998, -123.23106)) == ('Lighthouse Road, San Juan County, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((21.97472, -159.3656)) == ('Umi Street, Lihue, Kauai, Hawaiian Islands, Southwestern, Hawaii', 'Pacific/Honolulu'))
    assert(get_place_tz_from_latlon((22.08223, -159.76265)) == ('Polihale State Park, Kauaʻi County, Kauai, Hawaiian Islands, Southwestern, Beach, Hawaii', 'Pacific/Honolulu'))
    # Canada
    assert(get_place_tz_from_latlon((49.31168, -123.14786)) == ('Stanley Park, Vancouver, British Columbia, Canada', 'America/Vancouver'))
    assert(get_place_tz_from_latlon((48.56686, -123.46688)) == ('The Butchart Gardens, Central Saanich, Vancouver Island, British Columbia, Canada', 'America/Vancouver'))
    assert(get_place_tz_from_latlon((48.65287, -123.34463)) == ('Gulf Islands National Park Reserve, Southern Electoral Area, Sidney Island, British Columbia, Canada', 'America/Vancouver'))
    # Europe
    assert(get_place_tz_from_latlon((57.14727, -2.095665)) == ('Union Street, Aberdeen, Scotland', 'Europe/London'))
    assert(get_place_tz_from_latlon((57.169365, -2.101216)) == ('16 The Chanonry, Aberdeen, Scotland', 'Europe/London'))
    assert(get_place_tz_from_latlon((52.20234, 0.11589)) == ('Queens\' College (University of Cambridge), Newnham, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((48.858262, 2.293763)) == ('Eiffel Tower, Champ de Mars, Paris, Ile-de-France, France', 'Europe/Paris'))
    assert(get_place_tz_from_latlon((41.900914, 12.483172)) == ('Trevi Fountain, Fontana di, Municipio Roma I, Rome, Rione II, Lazio, Italy', 'Europe/Rome'))
    # Australasia
    assert(get_place_tz_from_latlon((-27.5014, 152.97272)) == ('Indooroopilly Shopping Centre, Brisbane City, Queensland, Australia', 'Australia/Brisbane'))
    assert(get_place_tz_from_latlon((-33.85733, 151.21516)) == ('Playhouse Theatre, Sydney Opera House, Upper Podium, New South Wales, Australia', 'Australia/Sydney'))
    assert(get_place_tz_from_latlon((27.17409, 78.04171)) == ('Taj Mahal Garden, Agra, Ganga Yamuna River Basin, Uttar Pradesh, India', 'Asia/Kolkata'))
    assert(get_place_tz_from_latlon((39.91639, 116.39023)) == ('Forbidden City, Xicheng District, Old, Beijing, China', 'Asia/Shanghai'))
    assert(get_place_tz_from_latlon((13.41111, 103.86234)) == ('Angkor Wat, Siem Reap, Cambodia', 'Asia/Phnom_Penh'))
    # Amenity/tourism/suburb
    assert(get_place_tz_from_latlon((47.62676944444444, -122.30770833333332)) == ('Saint Joseph Catholic Church, Capitol Hill, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.62659166666667, -122.30788333333334)) == ('Saint Joseph Catholic Church, Capitol Hill, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.6264, -122.3079)) == ('Saint Joseph School, Capitol Hill, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.62603888888889, -122.30757222222222)) == ('Saint Joseph School, Capitol Hill, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.66171666666666, -122.29951388888888)) == ('South Garage, University Village, District, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.66166388888889, -122.29971388888889)) == ('South Garage, University Village, District, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.59358888888889, -122.31080555555555)) == ('Seattle Bouldering Project - SBP, Washington', 'America/Los_Angeles'))
    # Slashes and parentheses
    assert(get_place_tz_from_latlon((47.593136111111114, -122.33296944444444)) == ('Lumen Field Event Center, International District Chinatown, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.59296388888889, -122.33313055555556)) == ('WaMu Theater, International District Chinatown, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((46.976708333333335, -120.17369722222223)) == ('L. T. Murray Wildlife Area (Whiskey Dick Unit), Kittitas County, Washington', 'America/Los_Angeles'))
    # Amenity/buildings/shops/retail/hamlet/historic
    assert(get_place_tz_from_latlon((47.82781944444445, -122.29219166666667)) == ('Lynnwood Recreation Center, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.82130555555556, -122.29823333333333)) == ('Arco, 4806 196th Street Southwest, Lynnwood, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.62366111111111, -122.33089444444444)) == ('Playdate SEA, South Lake Union, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.623675, -122.33113055555555)) == ('Playdate SEA, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.628819444444446, -122.34288888888888)) == ('Dexter Station, Westlake, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.62863611111111, -122.34264444444445)) == ('Dexter Station, Westlake, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.628825, -122.3429111111111)) == ('Dexter Station, Westlake, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.629019444444445, -122.34124722222222)) == ('Facebook Westlake, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.61843611111111, -122.13038611111111)) == ('WiggleWorks Kids, Bellevue, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.61853055555556, -122.1305)) == ('Crossroads, Lake Hills, Bellevue, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.662302777777775, -122.29841666666667)) == ('University Village, Coming Home, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((55.527425, -5.504425)) == ('Saddell Castle, Campbeltown, Scotland', 'Europe/London'))
    assert(get_place_tz_from_latlon((55.52716111111111, -5.505125)) == ('Saddell Castle, Campbeltown, Scotland', 'Europe/London'))
    assert(get_place_tz_from_latlon((55.527375, -5.503855555555556)) == ('Saddell Castle, Campbeltown, Firth of Clyde, Scotland', 'Europe/London'))
    assert(get_place_tz_from_latlon((55.42015, -5.604105555555555)) == ('Campbeltown Hospital, Dalintober, Scotland', 'Europe/London'))
    assert(get_place_tz_from_latlon((55.42106666666666, -5.603566666666667)) == ('Campbeltown Hospital, Dalintober, Scotland', 'Europe/London'))
    assert(get_place_tz_from_latlon((55.526913888888885, -5.504155555555555)) == ('Saddell Castle, Campbeltown, Firth of Clyde, Scotland', 'Europe/London'))
    assert(get_place_tz_from_latlon((55.424375, -5.6054916666666665)) == ('Bank of Scotland, Dalintober, Campbeltown, Scotland', 'Europe/London'))
    assert(get_place_tz_from_latlon((55.42735277777778, -5.605638888888889)) == ('Aqualibrum, Kinloch Public Park, Campbeltown, Scotland', 'Europe/London'))
    # Park
    assert(get_place_tz_from_latlon((47.54087777777777, -122.48220833333333)) == ('WA State Parks, Blake Island Marine Park, Kitsap County, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.54085277777778, -122.48735833333333)) == ('Blake Island Marine State Park, Kitsap County, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.5409, -122.4813)) == ('Blake Island Marine State Park Campground, Kitsap County, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.540863888888886, -122.48208611111112)) == ('Blake Island Marine State Park Campground, Kitsap County, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.6324, -122.3132)) == ('Volunteer Park Playground, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.632, -122.31341666666667)) == ('Volunteer Park Playground, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.64170555555555, -122.30924166666667)) == ('Montlake Playfield, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.6417, -122.3094)) == ('Montlake Community Center, Playfield, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.681777777777775, -122.24568888888889)) == ('The Fin Project From Swords into Plowshares, Seattle, Lake Washington, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.632191666666664, -122.29533333333333)) == ('Birches & Poplars, Washington Park Arboretum, Seattle, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.633716666666665, -122.29625833333333)) == ('Birches & Poplars, Washington Park Arboretum, Seattle, Washington', 'America/Los_Angeles'))
    # Wilderness
    assert(get_place_tz_from_latlon((47.2781, -121.3185)) == ('Meany Lodge, Kittitas County, Okanogan-Wenatchee National Forest, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.28511944444444, -121.31588611111111)) == ('Forest Road 5400-420, Kittitas County, Okanogan-Wenatchee National, Washington', 'America/Los_Angeles'))
    assert(get_place_tz_from_latlon((47.30723611111111, -121.31594166666666)) == ('Forest Road 54, Kittitas County, Okanogan-Wenatchee National, Washington', 'America/Los_Angeles'))
    # London
    assert(get_place_tz_from_latlon((51.470875, -0.4868722222222222)) == ('Heathrow Terminal 5, Wayfarer Road, London, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((51.481030555555556, -0.1787638888888889)) == ('Dartrey Walk, Worlds End, London, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((51.51676111111111, -0.13645277777777778)) == ('The London EDITION, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((51.51674166666667, -0.13426944444444444)) == ('1 Rathbone Square, London, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((51.5176, -0.1371)) == ('Sanderson Hotel, Fitzrovia, London, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((51.51056944444444, -0.13133611111111113)) == ('M&M\'s World, St. James\'s, London, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((51.51022777777778, -0.13242500000000001)) == ('W1D 111;W1D 311, St. James\'s, London, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((51.513755555555555, -0.13956388888888888)) == ('Shakespeare\'s Head, Soho, London, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((51.51390555555555, -0.13997777777777778)) == ('Liberty, Soho, London, England', 'Europe/London'))
    assert(get_place_tz_from_latlon((51.51343611111111, -0.07898333333333334)) == ('Guild Church of St Katharine Cree, 86 Leadenhall Street, London, England', 'Europe/London'))


if len(sys.argv) <= 1:
    print(f'Usage: {os.path.basename(__file__)} [files]')
elif sys.argv[1] == '--test-iso6709':
    test_iso6709()
elif sys.argv[1] == '--test-metadata':
    test_metadata()
elif sys.argv[1] == '--test-place':
    test_place()
elif sys.argv[1] == '--test':
    test_iso6709()
    test_metadata()
    test_place()
elif sys.argv[1] == '--debug':
    # I stick in here whatever I'm debugging at the moment
    print(get_place_tz_from_latlon((47.639483, -122.29801)))
else:
    try:
        (count_processed, count_error, count_renamed) = (0, 0, 0)
        for src in sys.argv[1:]:    
            (dir, srcname) = os.path.split(src)
            (srcname, ext) = os.path.splitext(srcname)
            pattern = re.compile('^\d\d\d\d.\d\d.\d\d - \d\d.\d\d.\d\d - (.*)$')
            match = pattern.match(srcname)
            stuff = match.group(1) if match else srcname
            (date, utc, latlon, err) = get_date_latlon(src)
            (stuff, tz) = (stuff, None) if latlon is None else get_place_tz_from_latlon(latlon)
            count_processed += 1
            if date is None and utc is not None and tz is not None:
                date = utc.replace(tzinfo=datetime.timezone.utc).astimezone(tz)
            if date is None and utc is not None:
                err = 'To convert utc, \'pip3 install pytz\'' if latlon is not None and tz is None and 'pytz' not in sys.modules else ''                    
                print(f'{srcname}{ext}  *** only has utc time; skipping. {err}')
                count_error += 1
                continue
            elif date is None:
                print(f'{srcname}{ext}  *** {err}', file=sys.stderr)
                count_error += 1
                continue
            # in case of filename clash, we'll append a suffix
            suffix = 1
            while True:
                dstname = f'{date.strftime("%Y.%m.%d - %H.%M.%S")} - {stuff}{"" if suffix == 1 else " "+str(suffix)}'
                dst = os.path.join(dir, dstname+ext)
                if os.path.exists(dst) and src != dst:
                    suffix += 1
                else:
                    break
            if src != dst:
                if err is None:
                    print(f'{dstname}{ext}')
                else:
                    print(f'{dstname}{ext}  *** {err}', file=sys.stderr)
                os.rename(src, dst)
                count_renamed += 1
        if count_processed == 0:
            print(f'No files to process', file=sys.stderr)
        elif count_error == 0 and count_renamed == 0:
            print(f'All {count_processed} photos were already correctly named', file=sys.stderr)
    except KeyboardInterrupt:
        sys.exit(130) # standard unix exit code for ctrl+c
