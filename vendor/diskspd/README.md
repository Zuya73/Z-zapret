# Microsoft DiskSpd 2.3

Unmodified amd64/diskspd.exe from the official v2.3 archive.
Source commit: 5e7025bfc9d1364f185d4c30963d7ac79195435e.
The pinned release archive and executable SHA256/size are recorded in manifest.json.
`python tools/verify_diskspd.py` validates the embedded source binary and x64 PE type.

MIT licence: LICENSE.txt. Copyright Microsoft. Source and release URLs are in the manifest.
The executable, manifest and licence are embedded in x2zapret. The installed/portable
package also contains licenses/DiskSpd-MIT.txt. No download or driver is required at run time.

The application uses only generated private regular files under a newly created
x2zapret-disk-test-<GUID> directory. No raw disk/partition target, arbitrary file path,
file-creation or file-truncation argument is exposed to DiskSpd. Files are fully filled
with random data before unbuffered measurements. Tests never use existing user data.
