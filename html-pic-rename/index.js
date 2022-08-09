/**
 * (c) 2022 Lucian Wischik
 * I hereby license this work under MIT and BSD licenses (you're free to pick which).
 */

const CLIENT_ID = '10118914-d9a5-4250-b05e-d130cd9d206d'; // from Azure app registrations https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade

let ACCESS_TOKEN=""; // provided by onedrive in the URL; initialized in "onload"
let USER_ID=""; // provided by onedrive in the URL; initialized in "onload"
let poisonCount=0; // is incremented for each file we successfully rename (to avoid infinite relogin loops)
let [currentIsCancelled, currentCancelCallbacks] = [false, []]; // every network fetch respects this callback
let currentFetchCallback = () => {}; // every network fetch calls this callback

function resetCurrentCancel() {
    const callbacks = currentCancelCallbacks;
    [currentIsCancelled, currentCancelCallbacks] = [false, []];
    for (const callback of callbacks) callback();
}
function cancelCurrentCancel() {
    const callbacks = currentCancelCallbacks;
    [currentIsCancelled, currentCancelCallbacks] = [false, []];
    for (const callback of callbacks) callback();
}

onload = async () => {
    const url = new URL(location.href);
    const params = new URLSearchParams(url.hash.replace(/^#/,''));
    ACCESS_TOKEN = params.get("access_token");
    USER_ID = params.get("user_id");
    if (!ACCESS_TOKEN) {
        document.getElementById("browser").style.display = 'none';
        document.getElementById("rename").style.display = 'none';
        document.getElementById("login").style.display = 'inline';
        return;
    }
    document.getElementById('logout').style.display = 'block';
    
    populateBrowser(0, null);
    try {
        const items = JSON.parse(await fetchStringAsync('', 'https://graph.microsoft.com/v1.0/me/drive/root/children?$top=10000&select=name,id,size,folder,file', false, false)).value;
        populateBrowser(0, items);
    } catch (e) {
        if (e.message === 'Unauthorized') relogin();
        else alert(e.message);
        return;
    }
    if (url.searchParams.has('selectedFolder')) {
        const selectedFolder = url.searchParams.get('selectedFolder').split(' ');
        const isRenaming = (document.cookie.includes('isRenaming=true'));
        document.cookie = "isRenaming=false";
        for (const folderId of selectedFolder) {
            await clickBrowser(folderId);
        }
        if (isRenaming && selectedFolder.length > 0) {
            renameFolder(selectedFolder.at(-1));
        }
    }
}

function relogin(isRenaming=false) {
    const redirect = new URL(`${location.pathname}${location.search}`, location.href);
    document.cookie = `isRenaming=${isRenaming}`;
    const url = `https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=${CLIENT_ID}&scope=files.readwrite&response_type=token&redirect_uri=${encodeURIComponent(redirect)}`;
    location.href = url;
}

/** Sends a GET request, including "Authorization: Bearer ${ACCESS_TOKEN}" if defined,
 * and returns the result as a string
 * @param {string} reason - human-readable explanation
 * @param {URL} url - the URL to fetch from
 * @param {boolean} {useRetryIfBusy} - should we retry in case of 429 "too many requests" or 504 "gateway timeout" (default false)
 * @param {boolean} {respectCancel} - should we respect the global doCancel method? (default true)
 * @returns {string} - the XMLHttpRequest.responseText returned
 */
async function fetchStringAsync(reason, url, useRetryIfBusy = false, respectCancel = true) {
    return await internalFetchAsync(reason, url, 'GET', undefined, undefined, useRetryIfBusy, respectCancel);
}

/** Sends a GET request for partial content, using "Range: bytes=..."
 * and including "Authorizatino: Bearer ${ACCESS_TOKEN}" if defined,
 * and returns the result as an ArrayBuf
 * @param {string} reason - human-readable explanation
 * @param {URL} url - the URL to fetch from
 * @param {number} start - start pos
 * @param {number} len - number of bytes to fetch
 * @param {boolean} {useRetryIfBusy} - should we retry in case of 429 "too many requests" or 504 "gateway timeout" (default false)
 * @param {boolean} {respectCancel} - should we respect the global doCancel method? (default true)
 * @returns {Uint8Array} - the XMLHttpRequest.response
 */
async function fetchBufAsync(reason, url, start, len, useRetryIfBusy = false, respectCancel = true) {
    return new Uint8Array(await internalFetchAsync(reason, url, 'GET', start, len, useRetryIfBusy));
}

/** Sends a HEAD request, including "Authorization: Bearer ${ACCESS_TOKEN}" if defined,
 * and returns the Content-Length response header parsed as an integer.
 * @param {string} reason - human-readable explanation
 * @param {URL} url - the URL to fetch from
 * @returns {number} - the parser value of the Content-Length header
 */
async function fetchContentLengthAsync(reason, url) {
    return await internalFetchAsync(reason, url, 'HEAD', undefined, undefined, false, true);
}

/** Sends a "verb" request (GET, POST, ...), including "Authorization: Bearer ${ACCESS_TOKEN}" if defined,
 * and returns the result as a string.
 * @param {string} reason - human-readable explanation
 * @param {URL} url - the URL to request
 * @param {string} verb - GET, POST, PATCH, ...
 * @param {string} requestBody - the request body
 * @param {string} contentType - the type of the request body, e.g. "application/json"
 * @returns {string} - the response
 */
 async function sendAndFetchStringAsync(reason, url, verb, requestBody, contentType) {
    return await internalFetchAsync(reason, url, verb, undefined, undefined, false, true, requestBody, contentType);
}

/** Helper for the above functions
 */
 function internalFetchAsync(reason, url, verb, start, len, useRetryIfBusy, respectCancel, requestBody, contentType) {
    if (respectCancel && currentIsCancelled) return Promise.reject(new Error('Cancelled'));
    if (typeof reason === 'string' && reason.length > 0) currentFetchCallback(reason);
    if (typeof reason !== 'string' || reason.startsWith('http')) {
        throw new Error('missing reason');
    }
    return new Promise((resolve, reject) => {
        try {
            if (respectCancel) {
                if (currentIsCancelled) reject(new Error('Cancelled'));
                else currentCancelCallbacks.push(() => reject(new Error('Cancelled')));
            }
            const xhr = new XMLHttpRequest();
            xhr.onreadystatechange = () => {
                try {                    
                    if (verb === 'HEAD' && xhr.readyState === xhr.DONE) {
                        resolve(parseInt(xhr.getResponseHeader('Content-Length')));
                    }
                } catch (e) {
                    reject(e);
                }
            };
            xhr.onload = () => {
                try {
                    if (verb === 'HEAD') {
                        return;
                    }
                    if (len) {
                        if (xhr.status === 206) resolve(xhr.response); else reject(new Error(xhr.statusText));
                    } else {
                        if (xhr.status === 200) resolve(xhr.responseText); else reject(new Error(xhr.statusText));
                    }
                } catch (e) {
                    reject(e);
                }
            };
            xhr.onerror = (e) => {
                reject(e);
            }
            xhr.open(verb, url, true);
            if (len) {
                xhr.responseType = 'arraybuffer';
                xhr.setRequestHeader('Range', `bytes=${start}-${start+len}`);
            }
            if (ACCESS_TOKEN) {
                xhr.setRequestHeader('Authorization', `Bearer ${encodeURIComponent(ACCESS_TOKEN)}`);
            }
            if (contentType) {
                xhr.setRequestHeader('Content-Type', contentType);
            }
            xhr.send(requestBody);
        } catch (e) {
            reject(e);
        }
    });
}

function populateBrowser(depth, items) {
    const browser = document.getElementById("browser");
    if (browser.children.length < depth) {console.log("missing parent"); return;}
    while (browser.children.length > depth) browser.removeChild(browser.children[depth]);
    const div = document.createElement('div');
    browser.appendChild(div);
    if (items === null) {
        const p = document.createElement('div');
        p.style.width='4ex';
        p.innerHTML = "<div class='spinner'></div>";
        div.appendChild(p);
        return;
    }
    for (const item of items) {
        if (!item.folder) continue;
        const p = document.createElement('p');
        p.innerText = item.name;
        p.dataset.depth = depth;
        p.dataset.id = item.id;
        p.dataset.name = item.name;
        p.dataset.size = item.size;
        p.dataset.selected = 0;
        p.onclick = () => clickBrowser(item.id);
        div.appendChild(p);
    }
    if (div.children.length === 0) {
        const fileCount = items.length - div.children.length;
        const p = document.createElement('p');
        p.innerText = `(${fileCount} files)`;
        p.style.cursor = 'auto';
        div.appendChild(p);
    }
}

async function clickBrowser(folderId) {
    const p = document.querySelector(`[data-id='${folderId}']`);
    // cancel any currently-underway renames
    cancelCurrentCancel();
    // adjust the 'rename' button
    const button = document.getElementById("rename");
    button.disabled = false;
    button.innerText = `Rename items in "${p.dataset.name}"...`;
    button.onclick = () => renameFolder(p.dataset.id);
    // select the item, and deselect the others
    const div = p.parentElement;
    for (var i = 0; i < div.children.length; i++) {
        div.children[i].dataset.selected = (div.children[i].dataset.id === p.dataset.id) ? 1 : 0;
    }
    // remove everything to the right, and update the location bar
    populateBrowser(parseInt(p.dataset.depth)+1, null);
    const selectedFolder = [...document.querySelectorAll("[data-selected='1']")].map(e => e.dataset.id).join(' ');
    const params = new URLSearchParams({selectedFolder});
    window.history.replaceState(null, null, `${location.pathname}?${params}${location.hash}`);
    // asynchronously populate the browser
    const items = JSON.parse(await fetchStringAsync('', `https://graph.microsoft.com/v1.0/me/drive/items/${p.dataset.id}/children?$top=10000&select=name,id,size,folder,file`, false, false)).value;
    populateBrowser(parseInt(p.dataset.depth)+1, items);
}

function log(html) {
    document.getElementById("log").insertAdjacentHTML('beforeend', html);
}

async function renameFolder(folderId) {
    resetCurrentCancel();
    document.getElementById('log').innerHTML = '';
    document.getElementById('rename').disabled = true;
    const p = document.querySelector(`[data-id='${folderId}']`);
    try {
        await renameFolderRec([p.dataset.name], folderId, 0, parseInt(p.dataset.size));
        log(`<span>All done.</span>`);
    } catch (e) {
        if (e.message === 'Unauthorized' && poisonCount > 0) relogin(true);
        else if (e.message === 'Cancelled') document.getElementById('log').innerHTML = '';
        else log(`<span class='error'>${e}</span>`);
    }
    document.getElementById('log').scrollIntoView({behavior: "smooth", block: "end", inline: "end"});
}

async function renameFolderRec(folderPath, folderId, bytesDone, bytesTotal) {
    log(`<h2><a href='https://onedrive.live.com/?id=${folderId}'>${folderPath.join(' &gt; ')}</a></h2>`);
    const items = JSON.parse(await fetchStringAsync('', `https://graph.microsoft.com/v1.0/me/drive/items/${folderId}/children?$top=10000&select=name,id,size,folder,file,webUrl,@microsoft.graph.downloadUrl`)).value;
    for (const item of items) {
        if (item.folder) {
            await renameFolderRec([...folderPath, item.name], item.id, bytesDone, bytesTotal);
        } else if ('@microsoft.graph.downloadUrl' in item) {
            log(`<table class='logitem' id='logitem_${item.id}'><tr><td rowspan='2' class='img'/><td class='name'/></tr><tr><td class='result'/></tr></table>`);
            const logitem = document.getElementById(`logitem_${item.id}`);
            const name = logitem.querySelector('.name');
            const img = logitem.querySelector('.img');
            const result = logitem.querySelector('.result');
            name.innerHTML = `<a href='${item.webUrl}'>${item.name}</a>`;
            document.getElementById('log').scrollIntoView({behavior: "smooth", block: "end", inline: "end"});
            if (/^\d\d\d\d\.\d\d\.\d\d - \d\d\.\d\d\.\d\d/.test(item.name)) {
                result.innerHTML = `<span class='info'>[left as is]</span>`;
                continue;
            }
            const formats = ['.jpg', '.jpeg', '.jp2', '.jpx', '.png', '.heic', '.heif', '.tif', '.tiff', '.gif', '.psd', '.webp', '.mp4', '.mov', '.avif', '.webm', '.mkv', '.flv', '.vob', '.ogv', '.ogg', '.drc', '.gifv', '.avi', '.qt', '.asf', '.amv', '.m4p', '.mpg', '.mp2', '.mpeg', '.mpe', '.mpv', '.m2v', '.m4v', '.3gp', '.3g2'];
            if (!formats.some(ext => item.name.toLowerCase().endsWith(ext))) {
                result.innerHTML = `<span class='info'>[not an image]</span>`;
                continue;
            }
            img.innerHTML = `<div class="spinner"></div>`;
            result.innerHTML = `<div class="spinner"></div> <span id="resolving">resolving...</span>`;
            let fetchCount = 0;
            currentFetchCallback = (s) => {
                fetchCount ++;
                const r = document.getElementById("resolving");
                if (r === null) return;
                r.innerText = `${s}#${fetchCount}`;
            };
            try {
                const downloadUrl = item['@microsoft.graph.downloadUrl'];
                const thumbnailPromise = fetchStringAsync('', `https://graph.microsoft.com/v1.0/me/drive/items/${item.id}/thumbnails/0/small`);
                const namePromise = calculateNameAsync(downloadUrl);
                thumbnailPromise.catch(() => {}); // since otherwise we might get runtime debugger complaints that no one caught the promise
                namePromise.catch(() => {});
                const thumbnail = JSON.parse(await thumbnailPromise);
                const [thumbnailUrl, thumbnailWidth, thumbnailHeight] = [new URL(thumbnail['url']), thumbnail['width'], thumbnail['height']];
                img.innerHTML = `<a href='${item.webUrl}'><img src='${thumbnailUrl}' style='width: ${thumbnailWidth}px; height: ${thumbnailHeight}px;'/></a>`;
                document.getElementById('log').scrollIntoView({behavior: "smooth", block: "end", inline: "end"});
                const [date, place, err] = await namePromise;
                if (err !== null) {
                    result.innerHTML = `<span class='error'>${err}</span>`;
                    continue;
                }
                if (item.name.includes(date)) {
                    result.innerHTML = `<span class='info'>[left as is]</span>`;
                    continue;
                }
                for (let iCandidate=1; ; iCandidate++) {
                    const oldName = item.name.replace(/^(.*)(\.[^\.]*)$/,'$1');
                    const oldExt = item.name.replace(/^(.*)(\.[^\.]*)$/,'$2');
                    const candidate = `${date} - ${place || oldName}${iCandidate === 1 ? '' : ` ${iCandidate}`}${oldExt}`;
                    const request = JSON.stringify({name: candidate});
                    try {
                        await sendAndFetchStringAsync('onedrive:rename', `https://graph.microsoft.com/v1.0/me/drive/items/${item.id}`, 'PATCH', request, 'application/json');
                        result.innerHTML = `${candidate}<br/><span class='info'>[renamed]</span>`;
                        break;
                    } catch (e) {
                        if (e.message === 'Conflict') continue;
                        throw e;
                    }
                    break;
                }
                poisonCount ++;
            } catch (e) {
                result.innerHTML = '';
                img.innerHTML = '';
                throw e;
            }
        }
        bytesDone += item.size;
    }
}

/** Parses an ISO6709 geolocation string like "+46.7888-124.0958" into a lat+lon pair [46.7888, -124.0958].
 * https://en.wikipedia.org/wiki/ISO_6709
 * The spec allows numbers to be decimal fractions like above, or degrees/minutes/seconds also with optional decimal fractions.
 * It also allows altitude, and accuracy.
 * This function returns null if not a valid geolocation string.
 * This function also accommodates some related formats like "+46.7888-124.0958+018.337/" which isn't strictly to spec, but is what iphoneXs produces
 * 
 * @param {string} s - the string to parse
 * @returns {[number,number]|null} - the latitude and longitude
 */
function parseIso6709(s) {
    const re = /^([+-])(\d\d|\d\d\d\d|\d\d\d\d\d\d)(\.\d*)?([+-])(\d\d\d|\d\d\d\d\d|\d\d\d\d\d\d\d)(\.\d*)?[+-/]/;
    const match = re.exec(s);
    if (!match) return null;
    // Longitude
    const sign1 = parseInt(match[1] + '1'); // number +1 or -1
    const frac1 = match[3] || ''; // string "" or ".23"
    const val1 = match[2]; // string "dd" or "dddd" or "dddddd"
    const lat = (val1.length === 2) ? sign1 * parseFloat(val1 + frac1)
      : (val1.length === 4) ? sign1 * ( parseFloat(val1.substring(0,2)) + parseFloat(val1.substring(2) + frac1) / 60.0)
      : sign1 * (parseFloat(val1.substring(0,2)) + parseFloat(val1.substring(2,4)) / 60.0 + (parseFloat(val1.substring(4) + frac1)) / 3600.0);
    // Latitude
    const sign2 = parseInt(match[4] + '1'); // number +1 or -1
    const frac2 = match[6] || ''; // string "" or ".23"
    const val2 = match[5]; // string "ddd" or "ddddd" or "ddddddd"
    const lon = (val2.length == 3) ? sign2 * parseFloat(val2 + frac2)
      : (val2.length === 5) ? sign2 * ( parseFloat(val2.substring(0,3)) + parseFloat(val2.substring(3) + frac2) / 60.0)
       : sign2 * ( parseFloat(val2.substring(0,3)) +parseFloat(val2.substring(3,5)) / 60.0 + parseFloat(val2.substring(5) + frac2) / 3600.0);
    // done
    return [lat, lon];
}

/** Given a buffer, extracts an ascii string. Bytes outside the 32-127 range are
 * expressed using normal javascript escapes, e.g. "\x89PNG\x)D\x0A\x1A\x0A"
 * @param {Uint8Array} buf - the array buffer
 * @param {number} pos - read bytes starting at this pos
 * @param {number} len - read this many bytes
 * @returns {string} - the ascii substring
 */
function getAscii(buf, pos, len) {
    const arr = new Uint8Array(buf);
    if (len > arr.length) throw new Error(`Attempted to read len=${len} of a ${arr.length} buffer`);
    let s = "";
    for (let i=0; i<len; i++) {
        s += String.fromCharCode(arr[pos+i]);
    }
    return s;
}

/** Reads an unsigned big-endian integer out of a buffer
 * @param {Uint8Array} buf - the raw buffer
 * @param {number} pos - read bytes starting at this pos
 * @param {number} len - either 2, 4 or 8, for how many bytes the integer is
 * @param {string} byteorder - either 'big' (default) or 'little'
 * @returns {number} - the uint that we read
 */
function getInt(buf, pos, len, byteorder = 'big') {
    if (byteorder !== 'big' && byteorder !== 'little') throw new Error(`unrecognized byteorder ${byteorder}`);
    // The 'buf' is a view over an underlying ArrayBuffer starting at buf.byteOffset in that ArrayBuffer.
    // We therefore want to read at 'pos + buf.byteOffset' in that underlying ArrayBuffer.
    const d = new DataView(buf.buffer, buf.byteOffset + pos);
    const isLittle = (byteorder === 'little');
    switch (len) {
        case 1: return d.getUint8(0, isLittle);
        case 2: return d.getUint16(0, isLittle);
        case 4: return d.getUint32(0, isLittle);
        case 8: return d.getBigUint64(0, isLittle);
        default: throw new Error(`Incorrect int length ${len}`);
    }
}

/** Reads a big-endian date out of a buffer. The meaning of this date is simply a number of seconds
 * since a (camera-defined) epoch, and it's stored in the buffer as either a 4-byte or 8-byte integer.
 * We return it as a Date object, i.e. something relative to the Javascript-defined epoch.
 * @param {Uint8Array} buf - the raw buffer
 * @param {number} pos - read bytes starting at this pos
 * @param {number} len - either 4 or 8, for how many bytes the integer is
 * @returns {Date|null} - the date that we read, or null if it failed
 */
function getDate(buf, pos, len, byteorder) {
    if (len !== 4 && len !== 8) throw new Error(`Incorrect date length ${len}`);
    const ms = getInt(buf, pos, len, byteorder) * 1000;
    if (ms === 0) {
        return null;
    }
    // COMPATIBILITY-BUG: The spec says that these are expressed in seconds since 1904.
    // But my brother's Android phone picks them in seconds since 1970.
    // I'm going to guess that all dates before 1970 should be 66 years in the future
    const TZERO_1904 = Date.UTC(1904,0,1,0,0,0);
    const TZERO_1970 = Date.UTC(1970,0,1,0,0,0);
    if (ms + TZERO_1904 < TZERO_1970) {
        return new Date(ms); // this ms presumably suffers the bug, and is ms since 1970, which is what Date constructor expects
    } else {
        return new Date(ms + TZERO_1904 - TZERO_1970); // convert the 1904-epoch into 1970-epoch for Date constructor
    }
}

/** Asynchronous function. Returns a promise for fetching information about the image/movie at the URL.
 * If size parameter is omitted, will first fetch the size from the url.
 * Returns a tuple [localDatetime?, utcDatetime?, latlon?, error?]
 * If it can get a local datetime, it will. Otherwise it'll try to get a utc date-time. Otherwise it will error.
 * It will try to get latitude and longitude. Otherwise it will error.
 * Error behavior is to return a non-null error string.
 */
async function fetchDateLatLonAsync(url, size) {
    // some file format pointers: http://nokiatech.github.io/heif/technical.html
    // heic: http://cheeky4n6monkey.blogspot.com/2017/10/monkey-takes-heic.html
    if (!(typeof size === 'number')) {
        size = await fetchContentLengthAsync('onedrive:size', url);
    }
    if (size < 8) return [null, null, null, 'file too small'];
    const buf = await fetchBufAsync('onedrive:header', url, 0, 8);
    const header = getAscii(buf, 0, 8);
    if (header.startsWith('\xFF\xD8')) return await fetchDateLatLonExifAsync(url, 0, size);
    else if (header.endsWith('ftyp')) return await fetchDateLatLonMp4Async(url, 0, size);
    else if (header === '\x89PNG\x0D\x0A\x1A\x0A') return await fetchDateLatLonPngAsync(url, 0, size);
    else return [null, null, null, `unrecognized header '${header}'`];
}

async function fetchDateLatLonPngAsync(url, pos, end) {
    // http://www.libpng.org/pub/png/spec/1.2/PNG-Structure.html#PNG-file-signature
    // http://ftp-osl.osuosl.org/pub/libpng/documents/pngext-1.5.0.html#C.eXIf
    // A series of chunks.
    if (pos !== 0) throw new Error('expected 0 pos for png');
    let date = null; // ?Date
    let next_pos = 8;
    while (true) {
        if (next_pos + 12 > end) break;
        const header_buf = await fetchBufAsync('onedrive:png.chunk', url, next_pos+0, 8);
        const length = getInt(header_buf, 0, 4, 'big');
        const type = getAscii(header_buf, 4, 4);
        if (next_pos + 12 + length > end) break;
        const chunk = next_pos;
        next_pos = next_pos + 12 + length;
        if (type === 'eXIf') {
            const buf = await fetchBufAsync('onedrive:png.eXIf', url, chunk+8, length);
            return getDateLatLonFromBom(buf);
        } else if (type === 'tEXt') {
            // payload is "key{latin1}, null-byte, val{latin1}"
            const buf = await fetchBufAsync('onedrive:png.tEXt', url, chunk+8, length);
            const inull = buf.indexOf(0);
            if (inull === -1) continue;
            const key = getAscii(buf, 0, inull); // TODO: latin1
            const value = getAscii(buf, inull+1, length-inull-1); // TODO: latin1
            if (key != 'date:create' && key != 'date:modify') continue;
            // PNG spec "suggests" to have form "2013-12-31T00:00:00Z" or "2013-12-31T00:00:00-08:00" but it might be freeform
            const matchA = /^(\d\d\d\d)-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)/.exec(item_value); // ignoring milliseconds and timezone
            const value_date = matchA ? new Date(Date.UTC(parseInt(matchA[1]), parseInt(matchA[2])-1, parseInt(matchA[3]), parseInt(matchA[4]), parseInt(matchA[5]), parseInt(matchA[6]))) : null;
            if (date === null || value_date < date) date = value_date;
        }
    }
    if (date === null) return [null, null, null, 'No eXIf or date found in PNG'];
    return (date, null, null, null);
}

function getDateLatLonFromBom(buf) {
    let latNS = null; // ?('N'|'S')
    let lonEW = null; // ?('E'|'W')
    let latNum = null; // ?number
    let lonNum = null; // ?number
    let timeLastModified = null; // ?Date
    let timeOriginal = null; // ?Date
    let timeDigitized = null; // ?Date
    let offsetLastModified = null; // ?string
    let offsetOriginal = null; // ?string
    let offsetDigitized = null; // ?string

    let bom = getAscii(buf, 0, 4);
    if (bom !== 'MM\x00*' && bom != 'II*\x00') return [null, null, null, `Exif unrecognized BOM ${bom}`];
    const byteorder = (bom === 'II*\x00') ? 'little' : 'big';
    let ipos = getInt(buf, 4, 4, byteorder);
    if (ipos + 12 >= buf.length) return [null, null, null, 'Exif marker size wrong'];
    // Format of EXIF is a chain of IFDs. Each consists of a number of tagged entries.
    // One of the tagged entries may be "SubIFDpos = &H..." which gives the address of the
    // next IFD in the chain; if this entry is absent or 0, then we're on the last IFD.
    // Another tagged entry may be "GPSInfo = &H..." which gives the address of the GPS IFD
    let subifdpos = 0;
    let gpsifdpos = 0;
    while (true) {
        const nentries = getInt(buf, ipos+0, 2, byteorder);
        if (10 + ipos + 2 + nentries*12 + 4 > buf.length) continue; // error in ifd header
        ipos_next = getInt(buf, ipos + 2 + nentries*12, 4, byteorder);
        for (let i=0; i<nentries; i++) {
            const epos = ipos + 2 + i * 12
            const tag = getInt(buf, epos+0, 2, byteorder);
            const format = getInt(buf, epos+2, 2, byteorder);
            const ncomps = getInt(buf, epos+4, 4, byteorder);
            const data = getInt(buf, epos+8, 4, byteorder);
            if (tag === 0x8769 && format === 4) subifdpos = data;
            else if (tag === 0x8825 && format === 4) gpsifdpos = data;
            else if ((tag === 1 || tag === 3) && format === 2 && ncomps === 2) {
                if (tag === 1) {
                    latNS = String.fromCharCode(data >> 24);
                    if (latNS !== 'N' && latNS !== 'S') return [null, null, null, `Wrong NS: ${latNS}`];
                } else if (tag === 3) {
                    lonEW = String.fromCharCode(data >> 24);
                    if (lonEW !== 'E' && lonEW !== 'W') return [null, null, null, `Wrong EW: ${lonEW}`];
                }
            }
            else if ((tag === 2 || tag === 4) && format === 5 && ncomps === 3 && 10 + data + ncomps <= buf.length) {
                const degTop = getInt(buf, data+0, 4, byteorder);
                const degBot = getInt(buf, data+4, 4, byteorder);
                const minTop = getInt(buf, data+8, 4, byteorder);
                const minBot = getInt(buf, data+12, 4, byteorder);
                const secTop = getInt(buf, data+16, 4, byteorder);
                const secBot = getInt(buf, data+20, 4, byteorder);
                const deg = degTop / degBot + minTop / minBot / 60.0 + secTop / secBot / 3600.0;
                if (tag === 2) latNum = deg; else lonNum = deg;
            } else if ((tag == 0x132 || tag === 0x9003 || tag === 0x9004) && format === 2 && ncomps === 20 && 10 + data + ncomps <= buf.length) {
                const s = getAscii(buf, data, ncomps-1);
                if (tag === 0x132) timeLastModified = s;
                else if (tag === 0x9003) timeOriginal = s;
                else if (tag === 0x9004) timeDigitized = s
            } else if ((tag === 0x9010 || tag === 0x9011 || tag === 0x9012) && format === 2 && 10 + data + ncomps <= buf.length) {
                const s = getAscii(buf, data, ncomps-1);
                if (tag === 0x9010) offsetLastModified = s;
                else if (tag === 0x9011) offsetOriginal = s;
                else if (tag == 0x9022) offsetDigitized = s;
            } else {
                // nothing to do for this block
            }
        }
        ipos = ipos_next;
        if (ipos === 0) {
            [ipos, subifdpos] = [subifdpos, 0];
            if (ipos === 0) {
                [ipos, gpsifdpos] = [gpsifdpos, 0];
                if (ipos === 0) {
                    break; // indicates the last IFD in this marker
                }
            }
        }
    }

    let date = null; // ?Date
    for (let [date_str, offset_str] of [[timeLastModified, offsetLastModified], [timeOriginal, offsetOriginal], [timeDigitized, offsetDigitized]]) {
        if (date_str === null) continue;
        try {
            // Exifdates have the form "2013:12:31 00:00:00", or "2013:12:31 00:00:00-08:00" - the former implicitly means a local time, and the latter is explicitly a local time
            // ECMAScript has the form "2013-12-31T00:00:00Z" or "2013-12-31T00:00:00-08:00" for its Date.parse format
            const re = /(\d\d\d\d):(\d\d):(\d\d) (\d\d):(\d\d):(\d\d)/;
            const match = re.exec(date_str);
            if (match) {
                const date_val = new Date(Date.UTC(parseInt(match[1]), parseInt(match[2])-1, parseInt(match[3]), parseInt(match[4]), parseInt(match[5]), parseInt(match[6])));
                if (date === null || date_val < date) date = date_val;
            }
        } catch (e) {
            continue;
        }
    }
    if (date === null) return [null, null, null, 'Exif lacks times'];

    let latlon = null; // ?[number,number]
    if (latNum !== null && latNS !== null && lonNum !== null && lonEW !== null) {
        lat = latNum * (latNS === 'N' ? 1 : -1);
        lon = lonNum * (lonEW === 'E' ? 1 : -1);
        latlon = [lat, lon];
    }

    return [date, null, latlon, null];
}


/** Note: we expect the caller has already verified that at pos there is a two-byte SOI "StartOfImage" marker 0xFFD8
 */
async function fetchDateLatLonExifAsync(url, pos, end) {
    // https://www.wikidata.org/wiki/Q26381818
    // http://cipa.jp/std/documents/e/DC-008-2012_E_C.pdf
    pos = pos + 2
    // iterate through EXIF markers
    while (true) { 
        const mbuf = pos
        if (mbuf+4 > end) {
            return [null, null, null, 'did not find TIFF Exif block'];
        }
        const buf = await fetchBufAsync('onedrive:exif', url, mbuf, 10);
        const marker = getAscii(buf, 0, 2);
        const msize = getInt(buf, 2, 2);
        if (mbuf + msize > end) {
            return [null, null, null, 'TIFF block size mismatch'];
        }
        pos += 2+msize;
        if (marker === '\xFF\xDA') {
            // image data follows this marker; we can stop our iteration
            return [null, null, null, 'did not find TIFF Exif block before image data'];
        } else if (marker === '\xFF\xE1' && msize >= 14 && getAscii(buf, 4, 4) === 'Exif' && getInt(buf, 8, 2) === 0) {
            const bom = await fetchBufAsync('onedrive:exif.bom', url, mbuf+10, msize);
            return getDateLatLonFromBom(bom);
        }
    }
}


/* Given the pos+end of a box, returns [box_kind:HexAscii, box_start, box_end, box_buf] of it.
 * The box_start, box_end are for the payload of the box (not including its size+kind header)
 * and box_buf is the content of that payload.
 * If there is no box (e.g. we're given end===pos, or it's malformed) then returns box_kind null.
 * 
 * @param {URL} url - the url to fetch. We return a buffer which contains payload size "min(actual_bytes_available, prefetch)"".
 * @param {number} pos - the start absolute position of the box within the url
 * @param {number} end - the end absolute position of the box (if same as pos, then no fetch is performed)
 * @param {number} [prefetch] - when fetching from the url, we'll fetch this many bytes of payload.
 * @returns {string|null, number, number, Uint8Array]} - the [box_kind, box_start, box_end, box_buf], where box_kind is null if invalid.
 */
async function fetchMp4BoxAsync(reason, url, pos, end, prefetch) {
    const eof = [null, 0, 0, new ArrayBuffer(0)];
    if (pos+8 > end) return eof;
    const prefetch_whole = (prefetch || 0) + 16; // we want our header in addition to the payload-prefetch
    const buf_whole = await fetchBufAsync(reason, url, pos, prefetch_whole);
    const size = getInt(buf_whole, 0, 4);
    const kind = getAscii(buf_whole, 4, 4);
    if (size !== 1) {
        const buf_payload = new Uint8Array(buf_whole.buffer, buf_whole.byteOffset + 8, Math.min(prefetch, buf_whole.buffer.byteLength-8));
        return (pos+size <= end && size !== 0) ? [kind, pos+8, pos+size, buf_payload] : eof;
    } else if (size === 1 && pos + 16 < end) {
        const size8 = Number(getInt(buf_whole, 8, 8));
        const buf_payload = new Uint8Array(buf_whole.buffer, buf_whole.byteOffset + 16, Math.min(prefetch, buf_whole.buffer.byteLength-16));
        return (pos+size8 <= end && size8 !== 0) ? [kind, pos+16, pos+size8, buf_payload] : eof;
    } else {
        return eof;
    }
}

/* Given kind:HexAscii, looks through the boxes until it finds one of the same kind,
 * and returns [box_kind:HexAscii, box_start, box_end, box_buf].
 * If there is no such box, returns [null, 0, 0, null].
 * @param {URL} url - the url to fetch
 * @param {number} pos - the start absolute position of the first box within the url
 * @param {number} end - the end absolute position of the last box within the url
 * @param {number} [prefetch] - when fetching from the url, we'll fetch max(prefetch,16) bytes for each box
 * @returns {[string|null, number, number, ArrayBuffer]} - the [box_kind, box_start, box_end, box_buf], where box_kind is null if invalid
 */
async function fetchMatchingMp4BoxAsync(url, kind, pos, end, prefetch) {
    const eof = [null, 0, 0, new ArrayBuffer(0)];
    while (true) {
        const [box_kind, box_start, box_end, box_buf] = await fetchMp4BoxAsync(`onedrive:mp4.${kind}`, url, pos, end, prefetch);
        if (box_kind === null) return eof;
        else if (box_kind === kind) return [box_kind, box_start, box_end, box_buf];
        else pos = box_end;
    }
}

async function fetchDateLatLonMp4Async(url, pos, end) {
    // official spec: https://mpeg.chiariglione.org/standards/mpeg-4/iso-base-media-file-format/text-isoiec-14496-12-5th-edition
    // readable spec: https://clanmills.com/exiv2/book/
    // Worked example: https://leo-van-stee.github.io/
    // The file is made up of a sequence of boxes, with a standard way to find size and FourCC "kind" of each.
    // Some box kinds contain a kind-specific blob of binary data. Other box kinds contain a sequence
    // of sub-boxes. You need to look up the specs for each kind to know whether it has a blob or sub-boxes.
    // We look for a top-level box of kind "moov", which contains sub-boxes, and then we look for its sub-box
    // of kind "mvhd", which contains a binary blob. This is where Creation/ModificationTime are stored.
    let latlon = null; // [float,float]?

    // HEIF files have meta.iinf which describes all their items
    // Here are example HIEF images: https://github.com/nokiatech/heif/tree/gh-pages/content
    // implementation: https://fossies.org/linux/Image-ExifTool/lib/Image/ExifTool/QuickTime.pm
    const [meta1_kind, meta1_pos, meta1_end] = await fetchMatchingMp4BoxAsync(url, 'meta', pos, end);
    const [iinf_kind, iinf_pos, iinf_end, iinf] = await fetchMatchingMp4BoxAsync(url, 'iinf', meta1_pos+4, meta1_end, 8);

    let item_ID_for_exif = null; // ?number
    if (iinf_end - iinf_pos >= 8) {
        const iinf_version = getInt(iinf, 0, 4);
        const iinf_item_count = getInt(iinf, 4, (iinf_version === 0) ? 2 : 4);
        let next_pos = iinf_pos + (iinf_version === 0 ? 6 : 8);
        while (true) {
            const [infe_kind, infe_pos, infe_end, infe] = await fetchMp4BoxAsync('onedrive:mp4.infe', url, next_pos, iinf_end, 12);
            next_pos = infe_end;
            if (infe_kind !== 'infe' || infe + 12 > infe_end) break;
            const infe_version = getInt(infe, 0, 4) >> 24;
            if (infe_version !== 2) break;
            const infe_item_ID = getInt(infe, 4, 2);
            const infe_item_type = getAscii(infe, 8, 4);
            if (infe_item_type === 'Exif') item_ID_for_exif = infe_item_ID;
        }
    }
    const [iloc_kind, iloc_pos, iloc_end, iloc_hdr] = await fetchMatchingMp4BoxAsync(url, 'iloc', meta1_pos+4, meta1_end);
    if (iloc_end - iloc_pos >= 8) {
        const [_k, _p, _e, iloc] = await fetchMp4BoxAsync('onedrive:mp4.iloc', url, iloc_pos - iloc_hdr.byteOffset, iloc_end, iloc_end-iloc_pos);
        const iloc_version = getInt(iloc, 0, 4) >> 24;
        const iloc_sizes = getInt(iloc, 4, 2);
        const iloc_offset_size = (iloc_sizes >> 12) & 0x0F;
        const iloc_length_size = (iloc_sizes >> 8) & 0x0F;
        const iloc_base_offset_size = (iloc_sizes >> 4) & 0x0F;
        const iloc_index_size = (iloc_sizes >> 0) & 0x0F;
        const iloc_items_count = getInt(iloc, 6, (iloc_version < 2) ? 2 : 4);
        let iloc_p = (iloc_version < 2) ? 8 : 10;
        let iloc_i = 0;
        while (iloc_version <= 2 && iloc_p+16 <= iloc_end && iloc_i < iloc_items_count) {
            const item_ID = getInt(iloc, iloc_p+0, (iloc_version < 2) ? 2 : 4);
            const construction_method = (iloc_version === 0) ? 0 : getInt(iloc, iloc_p + iloc_version*2, 2);
            const data_reference_index = getInt(iloc, iloc_p + iloc_version*2 + 2, 2);
            const base_offset = (iloc_base_offset_size === 0) ? 0 : getInt(iloc, iloc_p + iloc_version*2 + 4, iloc_base_offset_size);
            const extent_count = getInt(iloc, iloc_p + iloc_version*2 + 4 + iloc_base_offset_size, 2);
            const extent_size = iloc_offset_size + iloc_length_size + (iloc_version === 0 ? 0 : iloc_index_size);
            const extent_p = iloc_p + iloc_version*2 + 4 + iloc_base_offset_size + 2;
            iloc_p = extent_p + extent_count * extent_size;
            iloc_i += 1;
            if (item_ID !== item_ID_for_exif) continue; // we're only interested in exif
            if (construction_method !== 0 || data_reference_index !== 0 || extent_count !== 1 || base_offset !== 0) continue; // not yet implemented
            const extent_offset = getInt(iloc, extent_p + (iloc_version === 0 ? 0 : iloc_index_size), iloc_offset_size);
            const extent_length = getInt(iloc, extent_p + iloc_offset_size + (iloc_version === 0 ? 0 : iloc_index_size), iloc_length_size);
            if (extent_offset + extent_length > end) continue;
            const buf = await fetchBufAsync('onedrive:mp4.iloc', url, extent_offset, extent_length);
            const tag = getAscii(buf, 4, 4);
            if (tag !== 'Exif') continue;
            const bom = new Uint8Array(buf.buffer, buf.byteOffset+10);
            return getDateLatLonFromBom(bom);
        }
    }

    const [moov_kind, moov_pos, moov_end] = await fetchMatchingMp4BoxAsync(url, 'moov', pos, end);

    // The optional "moov.meta.ilst" is what iphoneXs uses
    // https://developer.apple.com/library/archive/documentation/QuickTime/QTFF/Metadata/Metadata.html
    const [meta_kind, meta_pos, meta_end] = await fetchMatchingMp4BoxAsync(url, 'meta', moov_pos, moov_end);
    const [keys_kind, keys_pos, keys_end, keys_hdr] = await fetchMatchingMp4BoxAsync(url, 'keys', meta_pos, meta_end);
    const [ilst_kind, ilst_pos, ilst_end] = await fetchMatchingMp4BoxAsync(url, 'ilst', meta_pos, meta_end);
    // assemble all the keys
    const allKeys = [['','']]; // List<[HexString, HexString]>. Index 0 is never used.
    if (keys_pos + 8 <= keys_end) {
        const [_k, _p, _e, keys] = await fetchMp4BoxAsync('onedrive:mp4.keys', url, keys_pos - keys_hdr.byteOffset, keys_end, keys_end-keys_pos);
        const key_count = getInt(keys, 4, 4);
        let kpos = 8;
        for (let ikey = 0; ikey < key_count; ikey++) {
            if (kpos + 8 > keys.length) break;
            const key_size = getInt(keys, kpos, 4);
            if (kpos + key_size > keys.length) break;
            const key_namespace = getAscii(keys, kpos+4, 4);
            const key_value = getAscii(keys, kpos+8, key_size-8);
            allKeys.push([key_namespace, key_value]);
            kpos += key_size;

        }
    }
    // walk through the ilst sub-boxes, looking for location+date
    if (ilst_pos + 16 <= ilst_end) {
        let item_pos_next = ilst_pos;
        let date = null; // ?Date
        while (true) {
            const [item_kind, item_pos, item_end] = await fetchMp4BoxAsync('onedrive:mp4.ilst.hdr', url, item_pos_next, ilst_end);
            const [_k, _p, _e, item] = await fetchMp4BoxAsync('onedrive:mp4.ilist.buf', url, item_pos_next, item_end, item_end-item_pos);
            if (item_kind === null || item_pos + 16 > item_end) break;
            item_pos_next = item_end;
            const ikey = (((item_kind.charCodeAt(0) * 256) + item_kind.charCodeAt(1)) * 256 + item_kind.charCodeAt(2)) * 256 + item_kind.charCodeAt(3);
            if (ikey === 0 || ikey >= allKeys.length) break;
            const [namespace, key] = allKeys[ikey];
            const item_type = getInt(item, 8, 4);
            const item_locale = getInt(item, 12, 4);
            const item_value = (item_type === 1) ? getAscii(item, 16, item.length-16) : null; // TODO: utf8
            if (key === 'com.apple.quicktime.location.ISO6709' && item_value !== null) {
                latlon = parseIso6709(item_value);
            } else if (key === 'com.apple.quicktime.creationdate' && item_value !== null) {
                // iphoneXs uses the form "2021-01-16T20:29:24-0800" which is local time, plus indication of timezone
                // apple docs give the example "12/31/2012", which is a local date
                const matchA = /^(\d\d\d\d)-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)/.exec(item_value); // ignoring milliseconds and timezone
                const matchB = /^(\d+)\/(\d+)\/(\d+)$/.exec(item_value);
                if (matchA) date = new Date(Date.UTC(parseInt(matchA[1]), parseInt(matchA[2])-1, parseInt(matchA[3]), parseInt(matchA[4]), parseInt(matchA[5]), parseInt(matchA[6])));
                else if (matchB) date = new Date(Date.UTC(parseInt(matchB[3]), parseInt(matchB[1])-1, parseInt(matchB[2])));
            }
        }
        if (date !== null) return [date, null, latlon, null];
    }

    // The optional "moov.udta.CNTH" binary blob consists of 8bytes of unknown, followed by EXIF data
    // If present, we'll use that since it provides GPS as well as time.
    const [udta_kind, udta_pos, udta_end] = await fetchMatchingMp4BoxAsync(url, 'udta', moov_pos, moov_end);
    const [cnth_kind, cnth_pos, cnth_end] = await fetchMatchingMp4BoxAsync(url, 'CNTH', udta_pos, udta_end);
    if (cnth_pos + 16 <= cnth_end) {
        return await fetchDateLatLonExifAsync(url, cnth_pos+8, cnth_end);
    }
    
    // The optional "moov.udta.©xyz" blob consists of len (2bytes), lang (2bytes), iso6709 gps (size bytes)
    const [cxyz_kind, cxyz_pos, cxyz_end, cxyz_hdr] = await fetchMatchingMp4BoxAsync(url, '\xA9xyz', udta_pos, udta_end, 2);
    if (cxyz_pos + 4 <= cxyz_end) {
        const cxyz_len = getInt(cxyz_hdr, 0, 2);
        if (cxyz_pos + 4 + cxyz_len <= cxyz_end) {
            const [_k, _p, _e, cxyz] = await fetchMp4BoxAsync('onedrive:mp4.cxyz.buf', url, cxyz_pos-cxyz_hdr.byteOffset, cxyz_end, cxyz_end-cxyz_pos);
            cxyz_str = getAscii(cxyz, 4, cxyz_len); // TODO: utf8
            latlon = parseIso6709(cxyz_str);
        }
    }

    // The "mvhd" binary blob consists of 1byte (version, either 0 or 1), 3bytes (flags),
    // and then either (if version=0) 4bytes (creation), 4bytes (modification)
    // or (if version=1) 8bytes (creation), 8bytes (modification)
    // In both cases "creation" and "modification" are big-endian number of seconds since 1st Jan 1904 UTC
    const [mvhd_kind, mvhd_pos, mvhd_end, mvhd] = await fetchMatchingMp4BoxAsync(url, 'mvhd', moov_pos, moov_end, 12);
    if (mvhd_pos + 20 <= mvhd_end) {
        const mvhd_version = getInt(mvhd, 0, 1);
        const mvhd_date_bytes = (mvhd_version === 0) ? 4 : 8;
        const creation_time = getDate(mvhd, 4, mvhd_date_bytes);
        const [ftyp_kind, ftyp_pos, ftyp_end, ftyp] = await fetchMatchingMp4BoxAsync(url, 'ftyp', pos, end, 4);
        // COMPATIBILITY-BUG: The spec says that these times are in UTC.
        // However, my Sony Cybershot merely gives them in unspecified time (i.e. local time but without specifying the timezone)
        // Indeed its UI doesn't even let you say what the current UTC time is.
        // I also noticed that my Sony Cybershot gives MajorBrand="MSNV", which isn't used by my iPhone or Canon or WP8.
        // I'm going to guess that all "MSNV" files come from Sony, and all of them have the bug.
        const major_brand = getAscii(ftyp, 0, 4); // e.g. "qt" for iphone, "MSNV" for Sony
        if (creation_time === null) {
            return [null, null, latlon, 'mp4 metadata is missing date'];
        } else if (major_brand === 'MSNV') {
            return [creation_time, null, latlon, null]; // the creation_time is a local time
        } else {
            return [null, creation_time, latlon, null]; // the creation_time is a UTC time
        }
    }

    // There are other optional blocks that may help, e.g. b'\xA9day' contains a local time
    // and a UTC time on some cameras. But they're rare enough that I won't bother.
    return [null, null, null, 'No metadata atoms'];
}

/** Helper function to console.log() the mp4 box hierarchy.
 * @param {URL} url - the url we're examining
 * @param {number} pos - the start pos to examine the hierarchy
 * @param {number} end - the end pos to examine the hierarchy
 * @param {string} [indent] - optional indent for each line to console.log()
 * @returns {void}
 */
async function fetchMp4HierarchyAsync(url, pos, end, indent) {
    if (!indent) indent = "";
    while (true) {
        const [box_kind, box_start, box_end, buf] = await fetchMp4BoxAsync('', url, pos, end, 32);
        if (box_kind === null) return;
        const len = Math.max(Math.min(box_end - box_start, 24),0)
        console.log(`${indent}${box_kind}:${box_start}-${box_end}:${getAscii(buf,0,len)}${len < box_end-box_start ? "..." : ""}`);
        // Does the box have child boxes in it? are the children pos from the start of the box?
        if (box_kind === 'mdat' || box_kind === 'ftyp' || box_kind === 'infe' || box_kind === 'iloc') {
            // we'll deliberately skip content for these boxes
        } else if (box_kind === 'meta' || box_kind === 'iref') {
            const version = getInt(buf, box_start-pos+0, 4);
            await fetchMp4HierarchyAsync(url, box_start+4, box_end, url+'  ');
        } else if (box_kind === 'iinf') {
            const version = getInt(buf, box_start-pos+0, 4);
            const itemCount = getInt(buf, box_start-pos+4, (version === 0) ? 2 : 4);
            await fetchMp4HierarchyAsync(url, box_start + (version == 0 ? 6 : 8), box_end, indent+'  ');
        } else {
            // I can't be bothered to hard-code every single other box type, so here let's
            // just blindly hope for the best... This will be wrong on many box kinds!
            await fetchMp4HierarchyAsync(url, box_start, box_end, indent+'  ');
        }
        pos = box_end
    }
}

/** Uses online services Nominatim and Overpass to put together some place-name descriptions, and fetch timezone.
 * Returns [description, timezone].
 */
async function fetchPlaceAsync([lat, lon]) {
    // We will store parts1/parts2 as e.g. [ ['city','London'], ['hamlet','Farrowhead']]

    const xget1 = (node, tag) => {
        const doc = node.ownerDocument || node;
        return doc.evaluate(`.//${tag}`, node, null, XPathResult.FIRST_ORDERED_NODE_TYPE).singleNodeValue;
    }
    const xgetN = (node, tag) => {
        const doc = node.ownerDocument || node;
        const s = doc.evaluate(`.//${tag}`, node, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE);
        const r = []; for (let i=0; i<s.snapshotLength; i++) r.push(s.snapshotItem(i));
        return r;
    };

    // Nominatim has pretty good breakdowns
    const parts1 = []; // same as parts
    const url1 = `https://nominatim.openstreetmap.org/reverse?accept-language=en&format=xml&lat=${lat.toFixed(7)}&lon=${lon.toFixed(7)}&zoom=18`;
    const raw1 = await fetchStringAsync('nominatim', url1, true);
    const xml1 = (new DOMParser()).parseFromString(raw1, "application/xml", null); // e.g. <reversegeocode><result>Here</result><addressparts><road>Here</road><country>There</country></addressparts></reversegeocode>
    const result1 = xget1(xml1, 'reversegeocode');
    const summary1 = result1.textContent;
    const addressparts1 = xget1(xml1, 'addressparts');
    for (const apart of addressparts1.childNodes) {
        if (apart.textContent) {
            const atag = ['leisure','aeroway','historic'].includes(apart.tagName) ? 'tourism' :
                         ['building', 'shop', 'retail', 'office', 'commercial'].includes(apart.tagName) ? 'amenity' :
                         ['hamlet'].includes(apart.tagName) ? 'suburb' :
                         apart.tagName;
            parts1.push([atag, apart.textContent]);
        }
    }
    // I disagree with the way London is stored...
    if (parts1.some(([key,val]) => key === 'state_district' && val === 'Greater London')) parts1.push(['city','London']);

    // Overpass provides some additional tags that are sometimes missing from Nominatim.
    const parts2 = []; // same as parts
    const url2 = `https://overpass-api.de/api/interpreter?data=is_in(${lat.toFixed(7)},${lon.toFixed(7)});out;`;
    const raw2 = await fetchStringAsync('overpass', url2);
    const xml2 = (new DOMParser()).parseFromString(raw2, 'application/xml', null);
    let tz2 = null; // ?string
    for (const area of [...xgetN(xml2, 'area'), ...xgetN(xml2, 'way')]) {
        // e.g. <area><tag k="admin_level" v="1"/><tag k="name" v='Creedon"/></area>
        const tags = new Map(); // e.g. "admin_level->1, name->Creedon"
        for (const tag of xgetN(area, 'tag')) {
            if ('k' in tag.attributes && 'v' in tag.attributes) tags.set(tag.attributes['k'].nodeValue, tag.attributes['v'].nodeValue);
        }
        tz2 = tz2 || tags.get('timezone') || null;
        // now we try to make sense of what this area is...
        const name = tags.get('name:en') || tags.get('name') || null;
        const area_type = tags.get('type') || null;
        const boundary = tags.get('boundary') || null;
        const admin_level = parseInt(tags.get('admin_level')) || null;
        if (name === null) continue;
        else if (area_type === 'boundary' && boundary === 'administrative' && admin_level !== null) {
            parts2.push([admin_level, name]);
        } else if (tags.has('building') || tags.has('amenity')) {
            parts2.push(['amenity', name]);
        } else if (tags.has('leisure') || tags.has('tourism')) {
            parts2.push(['tourism', name]);
        } else if (area_type === 'site' || (area_type === 'multipolygon' && name !== 'Great Britain')) {
            parts2.push(['multipolygon', name]);
        }
    }

    // Assemble all this into a name. Our challenge is to use heuristics that capture only the
    // key human-centric parts, and omit redundant information
    // https://wiki.openstreetmap.org/wiki/Tag:boundary%3Dadministrative#11_admin_level_values_for_specific_countries

    const parts = [...parts1, ...parts2];
    const partsdic = new Map(parts);
    const amenities = parts.flatMap(([key, name]) => key === 'amenity' ? [name] : []);
    const house_number = (partsdic.has('house_number') ? partsdic.get('house_number') + ' ' : '');
    const road = (partsdic.has('road') ? house_number + partsdic.get('road') : null);
    const tourism = parts.flatMap(([key, name]) => key === 'tourism' && !parts.some(([k,v]) => k === 'amenity' && v === name) ? [name] : []);
    const suburb = partsdic.get('neighbourhood') || partsdic.get('neighborhood') || partsdic.get('suburb') || partsdic.get('10') || partsdic.get('9') || null;
    const city = partsdic.get('town') || partsdic.get('city') || partsdic.get('8') || partsdic.get('7') || partsdic.get('county') || partsdic.get('6') || null;
    const multipolygons = parts.flatMap(([key, name]) => key === 'multipolygon' ? [name] : []);
    const state = partsdic.get('state') || partsdic.get('4') || partsdic.get('5') || null;
    const country = partsdic.get('country') || partsdic.get('2') || null;

    // Amenities, tourism and particularly multipolygons are always the most interesting parts -
    // they represent human-centric boundaries that are so important that someone went to the trouble
    // of manually marking them out and entering them into the databases.
    const keyparts = []; // List<string>
    const preferSuburbOverRoad = tourism.length > 0 ? null : amenities.length > 0 ? true : false; // ?boolean
    keyparts.push(...amenities);
    if (road !== null && (preferSuburbOverRoad === false || (preferSuburbOverRoad === true && suburb === null))) keyparts.push(road);
    keyparts.push(...tourism);
    if (suburb !== null && (preferSuburbOverRoad === true || (preferSuburbOverRoad === false && road === null))) keyparts.push(suburb);
    if (city !== null) keyparts.push(city);
    keyparts.push(...multipolygons);

    // To avoid repetition, remove all words that appear earlier too
    const preceding = []; // List<string>
    const unique = [];
    const strip_paren = (s) => s.replaceAll(/[()[\]]/g,'');
    // strip_parentheses = {ord(forbidden):'' for forbidden in '()[]'}
    for (const part of keyparts) {
        const words = [...part.split(' ').filter(s => s !== '' && !preceding.includes(strip_paren(s)))];
        preceding.push(...words.map(s => strip_paren(s)));
        if (words.length > 0) unique.push(words.join(' '));
    }
    if (state !== null && !unique.includes(state)) unique.push(state);
    if (country !== null && (state === null || (country !== 'United States' && country !== 'United Kingdom')) && !unique.includes(country)) unique.push(country);

    // Sanitize
    let place = unique.join(', ');
    place = place.replaceAll(/[\\/?%*:|]/g,' ').replaceAll(/  /g,' ');
    place = place.substring(0,120);
    return [place, tz2];
}

/** Given a URL, attempts to generate a date-string of the form "2012.12.13 - 23.59.59"
 * and a place-string of the form "24th Ave E, Seattle, Washington". Success is either
 * returning both strings (if the metadata has GPS) or only the first string (if it has
 * metadata but no GPS). Failure returns an error string.
 * @param {URL} url - the url of a candidate media file
 * @param {number} [size] - the filesize of this media file
 * @returns {[string|null,string|null,string|null]} - [datestring, placestring, errorstring]
 */
async function calculateNameAsync(url, size) {
    const [localDate, utcDate, latlon, err] = await fetchDateLatLonAsync(url,size);
    const [placeString, timeZone] = (latlon === null ? [null, null] : await fetchPlaceAsync(latlon));
    if (err !== null) return [null, null, err];
    if (localDate === null && utcDate === null) return [null, null, 'no timestamp'];
    if (localDate === null && utcDate !== null && latlon === null) return [null, null, 'no GPS'];
    if (localDate === null && utcDate !== null && timeZone === null) return [null, null, 'failed to retrieve timezone for GPS'];

    let [year, month, day, hour, minute, second] = [null, null, null, null, null, null]; // 1-based, e.g. Jan is 01 and Dec is 12; uses h23
    if (localDate !== null) {
        // meaning of "localDate" is that it's stored using Javascript's Date.UTC (and should be read using UTC accessors)
        // but it actually denotes a local time in whatever timezone is appropriate for the picture
        [year, month, day, hour, minute, second] = [localDate.getUTCFullYear(), localDate.getUTCMonth()+1, localDate.getUTCDate(), localDate.getUTCHours(), localDate.getUTCMinutes(), localDate.getUTCSeconds()];
    } else {
        // meaning of "utcDate" is that it's stored using Javascript's Date.UTC
        // and it truly denotes a UTC timestamp. Our task is to figure out what
        // local time it was inside "timeZone"...
        const options = {year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric", second: "numeric", hourCycle: "h23", timeZone};
        const s = utcDate.toLocaleString("en-US", options); // e.g. 6/31/2019, 14:11:12
        const match = s.match(/^(\d+)\/(\d+)\/(\d+), (\d+):(\d+):(\d+)$/);
        if (!match) throw new Error(`Unexpected locale string '${s}'`);
        [month, day, year, hour, minute, second] = match.slice(1).map(s => parseInt(s));
    }
    const pad2 = (s) => `00${s}`.slice(-2);
    const dateString = `${year}.${pad2(month)}.${pad2(day)} - ${pad2(hour)}.${pad2(minute)}.${pad2(second)}`;
    return [dateString, placeString, null];
}

/** Takes two arguments. Does structural equality on them. If useException then will throw on differences; otherwise will return null|string for ok|err
 * @param {any} actual - the actual value
 * @param {any} expected - the expected value
 * @param {boolean} [useException] - if true (default) will throw on difference; if false, will return a string explaining the difference
 * @returns {null|string} - if the same, returns null; if different, either returns that difference or throws
 */
function assertEq(actual, expected, useException=true) {
    let [err, ok] = [null, true]; // 'ok=false' will cause err to be set; 'err' is authoritative
    let p = (x) => {
        if (x === null) return 'null';
        if (typeof x === 'undefined') return 'undefined';
        return x.toString();
    }
    if (typeof actual !== typeof expected) {
        err = `Got ${typeof actual} '${p(actual)}', expected ${typeof expected} '${p(expected)}'`;
    } else if (typeof actual === 'number' || typeof actual === 'string') {
        ok = (actual.toString() === expected.toString());
    } else if (typeof actual === 'object') {
        if (actual === null && expected === null) {}
        else if (actual instanceof Date && expected instanceof Date) ok = (actual.getTime() === expected.getTime());
        else if (Array.isArray(actual) && Array.isArray(expected)) {
            if (actual.length !== expected.length) ok=false;
            for (let i=0; i<Math.min(actual.length, expected.length) && !err; i++) {
                const itemErr = assertEq(actual[i], expected[i], false);
                if (itemErr) err = `[${i}] ${itemErr}`;
            }
        }
        else {
            err = `Unhandled object ${expected.constructor.name} '${p(expected)}'`;
            ok = false;
        }
    } else {
        err = `Unhandled type ${typeof expected} '${p(expected)}'`;
    }
    if (!ok && !err) err = `Got ${p(actual)}, expected ${p(expected)}`;
    if (err && useException) throw new Error(err);
    return err;
}

function testIso6709() {
    assertEq(parseIso6709("+46.7888-124.0958+018.337/"), [46.7888,-124.0958])
    assertEq(parseIso6709("+00-025/"), [0,-25])
    assertEq(parseIso6709("+46+002/"), [46,2])
    assertEq(parseIso6709("+48.8577+002.295/"), [48.8577,2.295])
    assertEq(parseIso6709("+27.5916+086.5640+8850CRSWGS_84/"), [27.5916,86.5640])
    assertEq(parseIso6709("+90+000/"), [90,0])
    assertEq(parseIso6709("+00-160/"), [0,-160])
    assertEq(parseIso6709("-90+000+2800CRSWGS_84/"), [-90,0])
    assertEq(parseIso6709("+38-097/"), [38,-97])
    assertEq(parseIso6709("+40.75-074.00/"), [40.75,-74])
    assertEq(parseIso6709("+40.6894-074.0447/"), [40.6894,-74.0447])
    assertEq(parseIso6709("+1234.56-09854.321/"), [12.576,-98.90535])
    assertEq(parseIso6709("+123456.7-0985432.1/"), [12.582416666666667,-98.90891666666667])
    assertEq(parseIso6709("+27.5916+086.5640+8850/"), [27.5916,86.564])
    assertEq(parseIso6709("-90+000+2800/"), [-90,0])
    assertEq(parseIso6709("+40.75-074.00/"), [40.75,-74])
    assertEq(parseIso6709("+352139+1384339+3776/"), [35.36083333333333,138.7275])
    assertEq(parseIso6709("+35.658632+139.745411/"), [35.658632,139.745411])
    console.log("done");
}

async function testMetadata() {
    const base = new URL('https://unto.me/pic-rename/test/');
    assertEq(await fetchDateLatLonAsync(new URL('eg-android - 2013.11.23 - 12.49 PST.mp4', base)), [null, new Date(Date.UTC(2013,10,23,20,49,51)), null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-android - 2013.12.28 - 15.48 PST.jpg', base)), [new Date(Date.UTC(2013,11,28,15,48,42)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-android - 2013.12.28 - 15.48 PST.mp4', base)), [null, new Date(Date.UTC(2013,11,28,23,48,57)), null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-canon-ixus - 2013.12.15 - 07.30 PST.jpg', base)), [new Date(Date.UTC(2013, 11, 15, 7, 31, 41)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-canon-ixus - 2013.12.15 - 07.30 PST.mov', base)), [new Date(Date.UTC(2013, 11, 15, 7, 30, 58)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-canon-powershot - 2013.12.28 - 15.51 PST.jpg', base)), [new Date(Date.UTC(2013, 11, 28, 15, 51, 11)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-canon-powershot - 2013.12.28 - 15.51 PST.mov', base)), [new Date(Date.UTC(2013, 11, 28, 15, 51, 27)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-depstech - 2020.01.20 - 20.40 PST.jpg', base)), [null, null, null, 'Exif lacks times']);
    assertEq(await fetchDateLatLonAsync(new URL('eg-iphone4s - 2013.12.28 - 15.49 PST.jpg', base)), [new Date(Date.UTC(2013, 11, 28, 15, 50, 10)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-iphone4s - 2013.12.28 - 15.49 PST.mov', base)), [new Date(Date.UTC(2013, 11, 28, 15, 50, 22)), null, null, null]); // tz: days=-1, secs=57600
    assertEq(await fetchDateLatLonAsync(new URL('eg-iphone5 - 2013.12.09 - 15.21 PST.mov', base)), [new Date(Date.UTC(2013, 11, 9, 15, 21, 37)), null, null, null]); // tz: days=-1, secs=57600
    assertEq(await fetchDateLatLonAsync(new URL('eg-iphone5 - 2013.12.10 - 15.40 PST.jpg', base)), [new Date(Date.UTC(2013, 11, 10, 15, 39, 54)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-iphone6-gps.jpg', base)), [new Date(Date.UTC(2016,1,18,21,10,48)), null, [47.63614722222222,-122.30151388888889], null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-iphonexs - 2021.01.16 - 07.00 PST.png', base)), [new Date(Date.UTC(2021, 0, 16, 7, 0, 51)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-iphonexs - 2021.01.17 - 12.18 PST.heic', base)), [new Date(Date.UTC(2021, 0, 17, 12, 18, 23)), null, [46.79380555555555, -124.10501944444444], null]); // tz: days=-1, secs=57600
    assertEq(await fetchDateLatLonAsync(new URL('eg-iphonexs - 2021.01.17 - 20.29 PST.mov', base)), [new Date(Date.UTC(2021, 0, 16, 20, 29, 24)), null, [46.7888, -124.0958], null]); // tz: days=-1, secs=57600
    assertEq(await fetchDateLatLonAsync(new URL('eg-iphonexs-memory - 2021.01.25 - 19.15 PST.mov', base)), [null, new Date(Date.UTC(2021, 0, 26, 3, 15, 25)), null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-notapic.txt', base)), [null, null, null, "unrecognized header 'This is '"]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-screenshot.png', base)), [null, null, null, 'No eXIf or date found in PNG']);
    assertEq(await fetchDateLatLonAsync(new URL('eg-sony-cybershot - 2013.12.15 - 07.30 PST.jpg', base)), [new Date(Date.UTC(2013, 11, 15, 7, 32, 37)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-sony-cybershot - 2013.12.15 - 07.30 PST.mp4', base)), [new Date(Date.UTC(2013, 11, 15, 7, 31, 51)), null, null, null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-wm10-gps.jpg', base)), [new Date(Date.UTC(2016, 1, 15, 22, 20, 58)), null, [47.63564167544167, -122.30185414664444], null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-wm10.mp4', base)), [null, new Date(Date.UTC(2016, 1, 25, 4, 27, 35)), [47.6361, -122.3013], null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-wp8 - 2013.12.15 - 07.33 PST.jpg', base)), [new Date(Date.UTC(2013,11,15,7,32,50)), null, [47.63610444444444, -122.30139333333334], null]);
    assertEq(await fetchDateLatLonAsync(new URL('eg-wp8 - 2013.12.15 - 07.33 PST.mp4', base)), [null, null, null, 'mp4 metadata is missing date']);
    console.log("done");
}

async function testName() {
    const base = new URL('https://unto.me/pic-rename/test/');
    assertEq(await calculateNameAsync(new URL('eg-android - 2013.11.23 - 12.49 PST.mp4', base)), [null, null, 'no GPS']);
    assertEq(await calculateNameAsync(new URL('eg-android - 2013.12.28 - 15.48 PST.jpg', base)), ['2013.12.28 - 15.48.42', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-android - 2013.12.28 - 15.48 PST.mp4', base)), [null, null, 'no GPS']);
    assertEq(await calculateNameAsync(new URL('eg-canon-ixus - 2013.12.15 - 07.30 PST.jpg', base)), ['2013.12.15 - 07.31.41', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-canon-ixus - 2013.12.15 - 07.30 PST.mov', base)), ['2013.12.15 - 07.30.58', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-canon-powershot - 2013.12.28 - 15.51 PST.jpg', base)), ['2013.12.28 - 15.51.11', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-canon-powershot - 2013.12.28 - 15.51 PST.mov', base)), ['2013.12.28 - 15.51.27', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-depstech - 2020.01.20 - 20.40 PST.jpg', base)), [null, null, 'Exif lacks times']);
    assertEq(await calculateNameAsync(new URL('eg-iphone4s - 2013.12.28 - 15.49 PST.jpg', base)), ['2013.12.28 - 15.50.10', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-iphone4s - 2013.12.28 - 15.49 PST.mov', base)), ['2013.12.28 - 15.50.22', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-iphone5 - 2013.12.09 - 15.21 PST.mov', base)), ['2013.12.09 - 15.21.37', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-iphone5 - 2013.12.10 - 15.40 PST.jpg', base)), ['2013.12.10 - 15.39.54', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-iphone6-gps.jpg', base)), ['2016.02.18 - 21.10.48', '1830 24th Avenue East, Seattle, Washington', null]);
    assertEq(await calculateNameAsync(new URL('eg-iphonexs - 2021.01.16 - 07.00 PST.png', base)), ['2021.01.16 - 07.00.51', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-iphonexs - 2021.01.17 - 12.18 PST.heic', base)), ['2021.01.17 - 12.18.23', 'Pacific County, Washington', null]);
    assertEq(await calculateNameAsync(new URL('eg-iphonexs - 2021.01.17 - 20.29 PST.mov', base)), ['2021.01.16 - 20.29.24', 'Grayland Beach State Park, Pacific County, Washington', null]);
    assertEq(await calculateNameAsync(new URL('eg-iphonexs-memory - 2021.01.25 - 19.15 PST.mov', base)), [null, null, 'no GPS']);
    assertEq(await calculateNameAsync(new URL('eg-notapic.txt', base)), [null, null, 'unrecognized header \'This is \'']);
    assertEq(await calculateNameAsync(new URL('eg-screenshot.png', base)), [null, null, 'No eXIf or date found in PNG']);
    assertEq(await calculateNameAsync(new URL('eg-sony-cybershot - 2013.12.15 - 07.30 PST.jpg', base)), ['2013.12.15 - 07.32.37', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-sony-cybershot - 2013.12.15 - 07.30 PST.mp4', base)), ['2013.12.15 - 07.31.51', null, null]);
    assertEq(await calculateNameAsync(new URL('eg-wm10-gps.jpg', base)), ['2016.02.15 - 22.20.58', '1800 Boyer Avenue East, Seattle, Washington', null]);
    assertEq(await calculateNameAsync(new URL('eg-wm10.mp4', base)), ['2016.02.24 - 20.27.35', '1830 24th Avenue East, Seattle, Washington', null]);
    assertEq(await calculateNameAsync(new URL('eg-wp8 - 2013.12.15 - 07.33 PST.jpg', base)), ['2013.12.15 - 07.32.50', '1830 24th Avenue East, Seattle, Washington', null]);
    assertEq(await calculateNameAsync(new URL('eg-wp8 - 2013.12.15 - 07.33 PST.mp4', base)), [null, null, 'mp4 metadata is missing date']);
    console.log("done");
}

async function testPlace() {
    // United States
    assertEq(await fetchPlaceAsync([47.637922, -122.301557]), ['24th Avenue East, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.629612, -122.315119]), ['Black Sun, Volunteer Park, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.639483, -122.29801]), ['Pinetum, Washington Park Arboretum, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.65076, -122.302043]), ['University of Washington, Husky Stadium, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.668719, -122.38296]), ['WaFd Bank, Ballard, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.681006, -122.407513]), ['Shilshole Bay Marina, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.620415, -122.349463]), ['Space Needle, Seattle Center, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.609839, -122.342981]), ['Pike Place Market Area, Belltown, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.65464, -122.30843]), ['University of Washington, West Campus, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.64529, -122.13064]), ['Microsoft Building 25, Redmond East Campus, 15700 Northeast 39th Street, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([48.67998, -123.23106]), ['Lighthouse Road, San Juan County, Haro Strait, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([21.97472, -159.3656]), ['Wilcox Elementary School, 4319 Hardy Street, Lihue, Kauai, Hawaiian Islands, Southwestern, Hawaii', 'Pacific/Honolulu']);
    assertEq(await fetchPlaceAsync([22.08223, -159.76265]), ['Polihale State Park, Kaua\u02bbi County, Kauai, Hawaiian Islands, Southwestern, Beach, Hawaii', 'Pacific/Honolulu']);
    // Canada
    assertEq(await fetchPlaceAsync([49.31168, -123.14786]), ['Stanley Park, Vancouver, British Columbia, Canada', 'America/Vancouver']);
    assertEq(await fetchPlaceAsync([48.56686, -123.46688]), ['The Butchart Gardens, Central Saanich, Vancouver Island, Greater Victoria, British Columbia, Canada', 'America/Vancouver']);
    assertEq(await fetchPlaceAsync([48.65287, -123.34463]), ['Gulf Islands National Park Reserve, Southern Electoral Area, Sidney Island, British Columbia, Canada', 'America/Vancouver']);
    // Europe
    assertEq(await fetchPlaceAsync([57.14727, -2.095665]), ['City News Convenience, Merchant Quarter, Centre, Aberdeen, Scotland', 'Europe/London']);
    assertEq(await fetchPlaceAsync([57.169365, -2.101216]), ['Birse Manse, Old Aberdeen, City, Scotland', 'Europe/London']);
    assertEq(await fetchPlaceAsync([52.20234, 0.11589]), ['Queens\' College (University of Cambridge), Newnham, Cambridgeshire, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([48.858262, 2.293763]), ['Eiffel Tower, Field of Mars, Paris, Ile-de-France, France', 'Europe/Paris']);
    assertEq(await fetchPlaceAsync([41.900914, 12.483172]), ['Trevi Fountain, Municipio Roma I, Rome, Lazio, Italy', 'Europe/Rome']);
    // Australasia
    assertEq(await fetchPlaceAsync([-27.5014, 152.97272]), ['Indooroopilly Shopping Centre, Brisbane City, Queensland, Australia', 'Australia/Brisbane']);
    assertEq(await fetchPlaceAsync([-33.85733, 151.21516]), ['Sydney Opera House, Upper Podium, New South Wales, Australia', 'Australia/Sydney']);
    assertEq(await fetchPlaceAsync([27.17409, 78.04171]), ['Taj Mahal Mughal Garden, Agra, Ganga Yamuna River Basin, Uttar Pradesh, India', 'Asia/Kolkata']);
    assertEq(await fetchPlaceAsync([39.91639, 116.39023]), ['Forbidden City, Dongcheng District, Beijing, China', 'Asia/Shanghai']);
    assertEq(await fetchPlaceAsync([13.41111, 103.86234]), ['Angkor Wat, Siem Reap, Cambodia', 'Asia/Phnom_Penh']);
    // Amenity/tourism/suburb
    assertEq(await fetchPlaceAsync([47.62676944444444, -122.30770833333332]), ['Saint Joseph Catholic Church, Capitol Hill, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.62659166666667, -122.30788333333334]), ['Saint Joseph Catholic Church, Capitol Hill, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.6264, -122.3079]), ['Saint Joseph School, Capitol Hill, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.62603888888889, -122.30757222222222]), ['Saint Joseph School, Capitol Hill, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.66171666666666, -122.29951388888888]), ['South Garage, University Village, District, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.66166388888889, -122.29971388888889]), ['South Garage, University Village, District, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.59358888888889, -122.31080555555555]), ['Seattle Bouldering Project - SBP, Washington', 'America/Los_Angeles']);
    // Slashes and parentheses
    assertEq(await fetchPlaceAsync([47.593136111111114, -122.33296944444444]), ['Lumen Field Event Center, International District Chinatown, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.59296388888889, -122.33313055555556]), ['WaMu Theater, International District Chinatown, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([46.976708333333335, -120.17369722222223]), ['Whiskey Dick Wildlife Area, Kittitas County, Washington', 'America/Los_Angeles']);
    // Amenity/buildings/shops/retail/hamlet/historic
    assertEq(await fetchPlaceAsync([47.82781944444445, -122.29219166666667]), ['44th Avenue West, Lynnwood, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.82130555555556, -122.29823333333333]), ['Arco, 4806 196th Street Southwest, Lynnwood, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.62366111111111, -122.33089444444444]), ['Playdate SEA, South Lake Union, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.623675, -122.33113055555555]), ['Playdate SEA, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.628819444444446, -122.34288888888888]), ['Dexter Station, Westlake, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.62863611111111, -122.34264444444445]), ['Dexter Station, Westlake, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.628825, -122.3429111111111]), ['Dexter Station, Westlake, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.629019444444445, -122.34124722222222]), ['Facebook Westlake, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.61843611111111, -122.13038611111111]), ['WiggleWorks Kids, Bellevue, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.61853055555556, -122.1305]), ['WiggleWorks Kids, Bellevue, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.662302777777775, -122.29841666666667]), ['University Village, Coming Home, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([55.527425, -5.504425]), ['Saddell Castle, Campbeltown, Scotland', 'Europe/London']);
    assertEq(await fetchPlaceAsync([55.52716111111111, -5.505125]), ['Saddell Castle, Campbeltown, Scotland', 'Europe/London']);
    assertEq(await fetchPlaceAsync([55.527375, -5.503855555555556]), ['Saddell Castle, Campbeltown, Firth of Clyde, Scotland', 'Europe/London']);
    assertEq(await fetchPlaceAsync([55.42015, -5.604105555555555]), ['Campbeltown Hospital, Dalintober, Scotland', 'Europe/London']);
    assertEq(await fetchPlaceAsync([55.42106666666666, -5.603566666666667]), ['Campbeltown Hospital, Dalintober, Scotland', 'Europe/London']);
    assertEq(await fetchPlaceAsync([55.526913888888885, -5.504155555555555]), ['Saddell Castle, Campbeltown, Firth of Clyde, Scotland', 'Europe/London']);
    assertEq(await fetchPlaceAsync([55.424375, -5.6054916666666665]), ['Bank of Scotland, Dalintober, Campbeltown, Scotland', 'Europe/London']);
    assertEq(await fetchPlaceAsync([55.42735277777778, -5.605638888888889]), ['Aqualibrum, Kinloch Public Park, Campbeltown, Scotland', 'Europe/London']);
    // Park
    assertEq(await fetchPlaceAsync([47.54087777777777, -122.48220833333333]), ['Blake Island Marine State Park Campground, Kitsap County, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.54085277777778, -122.48735833333333]), ['Blake Island Marine State Park, Kitsap County, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.5409, -122.4813]), ['Blake Island Marine State Park Campground, Kitsap County, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.540863888888886, -122.48208611111112]), ['Blake Island Marine State Park Campground, Kitsap County, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.6324, -122.3132]), ['Volunteer Park Playground, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.632, -122.31341666666667]), ['Volunteer Park Playground, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.64170555555555, -122.30924166666667]), ['Montlake Playfield, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.6417, -122.3094]), ['Montlake Community Center, Playfield, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.681777777777775, -122.24568888888889]), ['The Fin Project From Swords into Plowshares, Seattle, Lake Washington, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.632191666666664, -122.29533333333333]), ['Birches & Poplars, Washington Park Arboretum, Seattle, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.633716666666665, -122.29625833333333]), ['Birches & Poplars, Washington Park Arboretum, Seattle, Washington', 'America/Los_Angeles']);
    // Wilderness
    assertEq(await fetchPlaceAsync([47.2781, -121.3185]), ['Meany Lodge, Kittitas County, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.28511944444444, -121.31588611111111]), ['Forest Road 5400-420, Kittitas County, Washington', 'America/Los_Angeles']);
    assertEq(await fetchPlaceAsync([47.30723611111111, -121.31594166666666]), ['Forest Road 54, Kittitas County, Washington', 'America/Los_Angeles']);
    // London
    assertEq(await fetchPlaceAsync([51.470875, -0.4868722222222222]), ['Heathrow Terminal 5, Walrus Road, London, Airport, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([51.481030555555556, -0.1787638888888889]), ['Dartrey Walk, World\'s End, London, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([51.51676111111111, -0.13645277777777778]), ['The London EDITION, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([51.51674166666667, -0.13426944444444444]), ['Meta, Covent Garden, London, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([51.5176, -0.1371]), ['Sanderson Hotel, Mayfair, London, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([51.51056944444444, -0.13133611111111113]), ['M&M\'s World, St. James\'s, London, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([51.51022777777778, -0.13242500000000001]), ['Royal Mail, St. James\'s, London, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([51.513755555555555, -0.13956388888888888]), ['Shakespeare\'s Head, Soho, London, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([51.51390555555555, -0.13997777777777778]), ['Liberty, Soho, London, England', 'Europe/London']);
    assertEq(await fetchPlaceAsync([51.51343611111111, -0.07898333333333334]), ['Guild Church of St Katharine Cree, 86 Leadenhall Street, London, England', 'Europe/London']);
    // done
    console.log("done");
}

async function testHierarchy() {
    const base = new URL('https://unto.me/pic-rename/test/');
    const url = new URL('eg-android - 2013.11.23 - 12.49 PST.mp4', base);
    const size = await fetchContentLengthAsync(url);
    await fetchMp4HierarchyAsync(url, 0, size);
}
