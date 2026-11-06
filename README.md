# DCST4 – DICOM C-Store Tester 4

![Version](https://img.shields.io/badge/version-1.0.0-blue) ![Platform](https://img.shields.io/badge/platform-.NET%20Framework%204.8%20WPF-512BD4) ![fo-dicom](https://img.shields.io/badge/fo--dicom-5.2.6-green) ![License](https://img.shields.io/badge/license-MIT-lightgrey)

A Windows tool for testing a PACS or any DICOM Storage SCP. It generates clearly marked test images that behave like the output of a real modality, and sends them with C-STORE.

Querying a Modality Worklist is out of scope: that is what [DMWLT4](https://github.com/droidgren/DMWLT4) is for.

![Main window after a run](screenshots/main.png)

> **Test images only.** Every image says `TEST IMAGE - NOT FOR DIAGNOSTIC USE` in its pixels and in Image Comments. Do not send them to a production system unless you know how to remove them again.

## Features

- **Realistic exams:** CT and MR stacks, CR, DX and US images, with the tags their IODs require, consistent slice geometry and believable timestamps.
- **Everything is automatic, and everything can be overridden:** patient, accession number, study ID, UIDs, date and time. The fields show what the next run will use.
- **Official test patients:** random Patient IDs come only from Skatteverket's list of test personnummer. Birth date and sex follow from the number.
- **Swedish names:** about 40% contain å, ä or ö, with Specific Character Set ISO_IR 100 or ISO_IR 192.
- **Visible verification:** patient name, patient ID, accession number, modality, series and instance number and timestamp are burned into the pixels, so you can compare them with the tags in the viewer.
- **Configurable images:** number of series and images, matrix from 64 to 4096, and 8 to 16 bits stored as the modality allows.
- **Transfer syntaxes:** Implicit VR LE, Explicit VR LE, JPEG Lossless (Process 14 SV1).
- **Full response logging:** status code, state, description, Error Comment, Offending Element and time for every image, including warnings and vendor-specific codes.
- **Save to disk:** instead of, or in addition to, sending, as a DICOM File-set with a DICOMDIR that viewers can open.
- **Connectivity:** Ping, DICOM Echo, TLS, and saved destination profiles.

## Install and build

Requires Windows with .NET Framework 4.8.

Open `DCST4.sln` in Visual Studio, or build from the command line:

```powershell
dotnet build DCST4.sln -c Release
```

The program is `src\DCST4.App\bin\Release\net48\`: `DCST4.exe`, `DCST4.exe.config` and the `libs\` folder, which can be copied anywhere together.

## Usage

1. Enter the server's AE title, host and port. Try **Ping** and **Dicom Echo**.
2. On the **Exam** tab, select modality and exam type, and how many images you want. 1 series with 1 image gives a single test image.
3. Optionally, enter your own values on the **Identifiers** tab by unticking **Auto**.
4. Click **Generate & Send** and follow each image in **Results**.
5. Compare the burned-in text with the tags in the PACS viewer.

F1 in the program opens the help.

### Transfer syntax behaviour

fo-dicom always proposes Implicit VR in a presentation context of its own, next to the selected syntax, and would silently convert to it if the selected one was not accepted. A test of JPEG Lossless could then pass against a PACS that cannot take it.

So by default, **the run stops with an error and sends nothing if the server does not accept the selected syntax.** Tick **Allow fallback to Implicit VR** to behave like a real modality instead. The run then finishes with a warning, and the **Syntax used** column shows what was really sent.

### Reading the results

| State | Meaning | Examples |
|---|---|---|
| Success | Stored | `0x0000` |
| Warning | Stored, but the server changed or dropped something | `0xB000`, `0xB006`, `0xB007` |
| Failure | Not stored. The run continues with the next image | `0xA7xx`, `0xA9xx`, `0xCxxx` |
| NotSent | No response, for example association rejected or timed out | |

### Saving to disk

The selected folder becomes a DICOM File-set:

```
<folder>\DICOMDIR
<folder>\DICOM\EX000001\S001\IM0001
```

DICOM limits each part of a file name to 8 upper-case characters with no extension, so the folders do not show patient details. The **Saved to** column shows where each image went.

Each run gets a new `EX` folder, so nothing is overwritten. Saving again to the same folder adds to the same DICOMDIR, which is rebuilt from all files in the File-set when each run ends, also if the run was cancelled or sending failed. Each record states its own Specific Character Set, so exams in ISO_IR 100 and ISO_IR 192 can share a DICOMDIR.

## Generated tags

| Group | Values |
|---|---|
| All images | Specific Character Set, SOP Class and Instance UID, Patient (Name, ID, Birth Date, Sex, Age), Study (UID, Date, Time, ID, Accession Number, Referring Physician, Description), Series (Modality, UID, Number, Date, Time, Description, Body Part, Protocol), Equipment (Manufacturer `DCST4`, Model, Institution, Station, Serial Number, Software Version), Image (Instance Number, Content and Acquisition Date and Time, Image Type, Image Comments, Burned In Annotation `YES`, Lossy Image Compression `00`, Window Center and Width) |
| CT | Frame of Reference, Patient Position, slice geometry, KVP, Tube Current, Exposure Time, Convolution Kernel, Rescale −1024 HU |
| MR | Frame of Reference, Patient Position, slice geometry, Scanning Sequence, Sequence Variant, Scan Options, TR, TE, Echo Train Length, Flip Angle, Field Strength, Imaging Frequency |
| CR | View Position, Imager Pixel Spacing, Patient Orientation, KVP, Exposure Time, Tube Current |
| DX | As CR, plus Presentation Intent, Presentation LUT Shape, Pixel Intensity Relationship, Rescale, Detector Type, Image Laterality |
| US | Single-frame grayscale B-mode (MONOCHROME2, 8 bits): Image Type with application and `0001`, Transducer Data and Type, Depth of Scan Field, Mechanical and Thermal Index, Sequence of Ultrasound Regions with calibration in cm |

Window Center and Width are calculated from the stored pixel values, so the burned-in text is readable when the image opens.

## Test personnummer

The list embedded in the program comes from Skatteverket's open data:

- **Source:** `https://skatteverket.entryscape.net/rowstore/dataset/b4de7df7-63c0-4e7e-bb59-1f156a591763`
- **Fetched:** 2026-11-06, 43,895 rows, 42,939 unique. The published data really does contain some numbers twice.
- **Validity:** all pass the date and check digit checks.
- **Refresh:** run `tools\Update-TestPersonnummer.ps1` and rebuild. Skatteverket adds new numbers each December.

Skatteverket says on [Testpersonnummer som öppen data](https://www.skatteverket.se/omoss/digitalasamarbeten/omvaraoppnadata/testpersonnummersomoppendata.4.5b35a6251761e6914202df9.html) that these numbers will never be used for real persons, that they are blocked from being assigned, and that anyone may use them. Its open data is described as free to use without agreements or fees. No named licence such as CC0 was found on the pages checked, so that is worth confirming before the list is redistributed outside your organisation.

The list holds future dates and people born in the 1890s, so random selection only uses people already born and within an age range, 0 to 100 by default. A manually entered Patient ID that is not on the list is accepted with a warning.

## Architecture

```
src\DCST4.Core\     No WPF reference
  Generation\       Exam request, identity, modality profiles, pixel renderer, image builder
  TestData\         Test personnummer, names, identifiers
  Pipeline\         IImageSource, StoreRun (transcode, save, send), RunSummary
  Network\          Ping, C-ECHO, C-STORE
  Output\           DICOM File-set and DICOMDIR
src\DCST4.App\      WPF app
tests\DCST4.Tests\  Console test harness and mock Storage SCP
tools\              List refresh script
```

Generation knows nothing about sending. The two meet at `IImageSource`, which supplies images lazily so that a long stack does not sit in memory. That leaves room for:

| Possible addition | Where it plugs in |
|---|---|
| Sending existing files from a folder, with optional new UIDs | A new `IImageSource`. Sending does not change. |
| Load testing (send N times, measure throughput) | Repeat `StoreRun` with a new source. `RunSummary` already has timing, bytes and images per second. |
| Storage Commitment | `RunSummary.Results` keeps each image's SOP Class and Instance UID. |
| MPPS | `ExamRequest` holds the study and series identity. `Network\` gets a new service. |

To add a modality, subclass `ModalityProfile` and add it to `ModalityCatalog`.

## Tests

```powershell
.\tests\run-tests.ps1
```

A console harness in the same style as DMWLT4's, running the real code against a mock Storage SCP. Exit code 0 means all checks passed.

Other ways to use it:

```powershell
# Write sample images as .dcm and .png, for a visual check
.\tests\run-tests.ps1 -Render C:\Temp\samples

# Run the mock SCP for trying the app by hand. "mixed" scripts warnings and failures. It stores nothing.
tests\DCST4.Tests\bin\Release\net48\DCST4.Tests.exe scp 11112 mixed
```

JPEG Lossless streams are checked with a small decoder written from ITU T.81 (`LosslessJpegDecoder.cs`). fo-dicom's built-in decoder is not used as the reference, because it was seen returning 0 for the last pixel of a frame that this decoder and PureCodecs both decode correctly.

### Checking with another toolkit

The test tool can feed other DICOM software:

```powershell
# Every exam type, bit depth and transfer syntax as files. The same image in each syntax, so pixels can be compared.
tests\DCST4.Tests\bin\Release\net48\DCST4.Tests.exe matrix C:\Temp\matrix

# File-sets with a DICOMDIR: every modality in both character sets, uncompressed and JPEG
tests\DCST4.Tests\bin\Release\net48\DCST4.Tests.exe fileset C:\Temp\fileset

# Send one exam to any SCP: host, port, called AE, modality, images, bits, implicit|explicit|jpeg, optionally fallback
tests\DCST4.Tests\bin\Release\net48\DCST4.Tests.exe send 127.0.0.1 11112 MYSCP CT 60 16 jpeg
```

Version 1.0.0 was checked once by hand with DCMTK 3.7.0. This is not part of `run-tests.ps1`, which has no outside dependency:

- 324 files in the matrix: `dcmftest` and `dcmdump` pass with no warnings, and VR, VM and value formats match DCMTK's dictionary.
- All 108 JPEG files decode with `dcmdjpeg` to pixels identical to the uncompressed versions.
- The DICOMDIR passes `dcmftest` and `dcmdump`, `dcmmkdir` can append to it and accepts every file, and a separate byte-level walk confirms the record offsets, hierarchy, required keys, File IDs and that each record matches its file.
- `dcmconv` reads the name correctly in both ISO_IR 100 and ISO_IR 192.
- Sending to `storescp` works in all three syntaxes, a 60-image stack uses one association, and a default `storescp`, which does not accept JPEG, gets the error or the fallback as described above.

DCMTK has no IOD validator, so this does not prove that every attribute an IOD requires is present. Those are checked against a list in the tests, not by an outside tool.

## Dependencies

| Package | Use | Licence |
|---|---|---|
| fo-dicom 5.2.6 | DICOM | MS-PL |
| FoDicom.PureCodecs 0.5.1 | JPEG Lossless encoder. Managed code, no native DLL. Pre-1.0. | MS-PL |
| Serilog | Logging | Apache-2.0 |

## Files

| What | Where |
|---|---|
| Settings | `%LocalAppData%\DCST4\user.config` |
| Log, kept for 7 days | `%LocalAppData%\DCST4\Logs\` |

## Changelog

### 1.0.0

New:
- First version: generated CT, MR, CR, DX and US images with burned-in text, sent with C-STORE or saved to disk as a File-set with a DICOMDIR
- Auto or manual values for all identifiers, with test personnummer from Skatteverket
- Implicit VR LE, Explicit VR LE and JPEG Lossless, with optional fallback
- Per-image response logging, live log and run summary
- Ping, DICOM Echo, TLS and destination profiles from DMWLT4

## License

MIT, see [LICENSE](LICENSE).
