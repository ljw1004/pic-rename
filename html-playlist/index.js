/**
 * (c) 2022 Lucian Wischik
 * I hereby license this work under MIT and BSD licenses (you're free to pick which).
 */

const CLIENT_ID = '453f161a-f999-4342-9d7d-e0ed91772e1d';

let ACCESS_TOKEN=""; // provided by onedrive in the URL; initialized in "onload"
let USER_ID=""; // provided by onedrive in the URL; initialized in "onload"
let MUSIC_FOLDER_ID=""; // discovered inside "onload", assuming we're logged in

onload = async () => {
    const url = new URL(location.href);
    const params = new URLSearchParams(url.hash.replace(/^#/,''));
    ACCESS_TOKEN = params.get("access_token");
    USER_ID = params.get("user_id");
    if (!ACCESS_TOKEN) {
        document.getElementById("generate").style.display = 'none';
        document.getElementById("login").style.display = 'inline';
        return;
    }

    try {
        MUSIC_FOLDER_ID = JSON.parse(await fetchStringAsync('GET', `https://graph.microsoft.com/v1.0/me/drive/special/music`)).id;
    } catch (e) {
        if (e.message === 'Unauthorized') relogin();
        else alert(e.message);
        return;
    }
    try {
        showPlaylistUrl(JSON.parse(await fetchStringAsync('GET', `https://graph.microsoft.com/v1.0/me/drive/items/${MUSIC_FOLDER_ID}:/playlist.m3u`)));
    } catch (e) {
        // probably doesn't exist
    }
}

/** Shows an href link for the playlist.m3u file.
 * @param {any} json - this is the json returned by onedrive from a GET/POST. It must have ['@microsoft.graph.downloadUrl']
 */
function showPlaylistUrl(json) {
    const url = json['@microsoft.graph.downloadUrl'];
    document.getElementById('playlist').href = url;
    document.getElementById('playlistid').style.display = 'block';
}

function relogin() {
    location.href = `https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=${CLIENT_ID}&scope=files.readwrite&response_type=token&redirect_uri=${encodeURIComponent(location.origin + location.pathname)}`;
}

async function generate() {
    try {
        document.getElementById('generate').disabled = true;
        document.getElementById('playlistid').style.display = 'none';

        // We'll share the music folder, allowing public links to any item inside it
        const driveId = (JSON.parse(await fetchStringAsync('GET', `https://graph.microsoft.com/v1.0/me/drive`))).id;
        const linkRequest = JSON.stringify({type: 'view', scope: 'anonymous'});
        const shareUrl = new URL(JSON.parse(await fetchStringAsync('POST', `https://graph.microsoft.com/v1.0/me/drive/items/${MUSIC_FOLDER_ID}/createLink`, linkRequest, 'application/json')).link.webUrl);        
        const redirectUrl = new URL(await fetchStringAsync('GET', `/get-onedrive-redirect/?share=${shareUrl.pathname.replace(/^\//,'')}`, null, null, null));
        // shareUrl is like 'https://1drv.ms/u/s!abcdef'. We send it to 'https://unto.me/get-onedrive-redirect/?share=u/s!abcdef'.
        // This will fetch the shareUrl, intercept the 301 response, and give us back the Location header of that 301 as the response body,
        // e.g. 'https://onedrive.live.com/redir?resid=abc&authkey=def'
        const [resid, authkey] = [redirectUrl.searchParams.get('resid'), redirectUrl.searchParams.get('authkey')];
        // the following function, given a path under the Music folder like ['Blues', 'Beat Box.mp3'], generates a shared onedrive link to it.
        const link = (path) => `https://api.onedrive.com/v1.0/drives/${driveId}/items/${resid}:/${path.map(encodeURIComponent).join('/')}:/content?authKey=${authkey}`;

        // Walk the Music folder and synthesize an m3u out of it
        const size = JSON.parse(await fetchStringAsync('GET', `https://graph.microsoft.com/v1.0/me/drive/items/${MUSIC_FOLDER_ID}`)).size;
        const paths = []; // List<List<string>>. Each item in 'files' is a list of path components relative to MUSIC_FOLDER_ID, ending in the filename
        await walkFolderAsync(paths, [], MUSIC_FOLDER_ID, 0, size, Date.now());
        const items = paths.map(path => `#EXTINF:0,${path.join(' > ')}\n${link(path)}\n`);
        const m3u = `#EXTM3U\n${items.join('')}`;

        // Save the playlist
        const uploadResults = JSON.parse(await fetchStringAsync('PUT', `https://graph.microsoft.com/v1.0/me/drive/items/${MUSIC_FOLDER_ID}:/playlist.m3u:/content`, m3u, 'audio/mpegurl'));
        showPlaylistUrl(uploadResults);
        log('Done');
    } catch (e) {
        log(`ERROR - ${e.message}`);
    }
}

function log(s) {
    document.getElementById('log').innerText = s;    
}

/** Recursively walks the folder, accumulating full path of every music filename it encounters
 * @param {string[]} acc - the files it's accumulated so far
 * @param {string[]} path - current path, unescaped
 * @param {string} folderId - the current folder
 * @param {number} bytesSoFar - how many bytes we've done (for display purposes)
 * @param {number} bytesTotal - total bytes (for display purposes)
 * @returns {void}
 */
async function walkFolderAsync(acc, path, folderId, bytesSoFar, bytesTotal, startMs) {
    const items = JSON.parse(await fetchStringAsync('GET', `https://graph.microsoft.com/v1.0/me/drive/items/${folderId}/children?$top=10000&select=name,id,size,folder,file,webUrl`)).value;
    for (const item of items) {
        const itemPath = [...path, item.name];
        if (item.folder) {
            await walkFolderAsync(acc, itemPath, item.id, bytesSoFar, bytesTotal, startMs);
        } else if (item.file) {
            const formats = ['.3gp', '.aac', '.aiff', '.alac', '.au', '.flac', '.m4a', '.m4b', '.m4p', '.mp3', '.ogg', '.oga', '.mogg', '.opus', '.ra', '.rm', '.raw', '.voc', '.vox', '.wav', '.wma', '.wv', '.webm'];
            if (!formats.some(ext => item.name.toLowerCase().endsWith(ext))) continue;
            acc.push(itemPath);
        }
        bytesSoFar += item.size;

        const elapsedSecs = (Date.now() - startMs) / 1000;
        const remainingSecs = elapsedSecs * (bytesTotal - bytesSoFar) / bytesSoFar;
        log(`Scanned ${acc.length} files in ${Math.round(elapsedSecs)}s [${Math.round(bytesSoFar/bytesTotal*100)}%], ${Math.round(remainingSecs)}s remaining. ${path.join(' > ')}`);
    }
}

/** Sends a "verb" request (GET, POST, ...), including "Authorization: Bearer ${ACCESS_TOKEN}" if defined,
 * and returns the result as a string.
 * @param {URL} url - the URL to request
 * @param {string} verb - GET, POST, PATCH, ...
 * @param {string|URL} url - the URL to fetch
 * @param {string} [requestBody] - the request body
 * @param {string} [requestContentType] - the type of the request body, e.g. "application/json"
 * @param {string} [authorizationBearer] - value for "Authorization: Bearer ..." header - defaults to global variable ACCESS_TOKEN
 * @returns {string} - the XMLHttpRequest.responseText returned
 */
 function fetchStringAsync(verb, url, requestBody, requestContentType, authorizationBearer = ACCESS_TOKEN) {
    return new Promise((resolve, reject) => {
        try {
            const xhr = new XMLHttpRequest();
            xhr.onload = () => {
                try {
                    if (xhr.status === 200 || xhr.status === 201) resolve(xhr.responseText);
                    else reject(new Error(xhr.statusText));
                } catch (e) {
                    reject(e);
                }
            };
            xhr.onerror = (e) => {
                reject(e);
            }
            xhr.open(verb, url, true);
            if (authorizationBearer) {
                xhr.setRequestHeader('Authorization', `Bearer ${encodeURIComponent(authorizationBearer)}`);
            }
            if (requestContentType) {
                xhr.setRequestHeader('Content-Type', requestContentType);
            }
            xhr.send(requestBody);
        } catch (e) {
            reject(e);
        }
    });
}
