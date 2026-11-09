# DCST4 – DICOM C-Store Tester 4

![Version](https://img.shields.io/badge/version-1.4.0-blue) ![Platform](https://img.shields.io/badge/platform-.NET%20Framework%204.8%20WPF-512BD4) ![fo-dicom](https://img.shields.io/badge/fo--dicom-5.2.6-green) ![License](https://img.shields.io/badge/license-MIT-lightgrey)

A Windows tool for testing a PACS or any DICOM Storage SCP. It generates clearly marked test images that behave like the output of a real modality, and sends them with C-STORE.

For querying a Modality Worklist check out [DMWLT4](https://github.com/droidgren/DMWLT4) .

![Main window after a run](screenshots/main.png)


## Features

- **Zero Dependencies:** Built on .NET 4.8 (included natively in Windows 10 and 11). No external runtime installation is required.
- **Realistic exams:** CT, MR and PT stacks, CR, DX, US, XA, RF, MG and NM images, with the tags their IODs require, consistent slice geometry and believable timestamps.
- **Everything is automatic, and everything can be overridden:** patient, accession number, study ID, UIDs, date and time. The fields show what the next run will use.
- **Additional tags:** add common tags with generated values, or any custom tag, including private ones, under **File > Settings**.
- **Official test patients:** random Patient IDs come only from Skatteverket's list of test personnummer. Birth date and sex follow from the number.
- **Swedish names:** about 40% contain å, ä or ö, with Specific Character Set ISO_IR 100 or ISO_IR 192. An empty Specific Character Set (Basic ASCII) is also available, and random names then lose their diacritics.
- **Optional burned-in text:** turn on a text box in the pixels and choose which tags it shows, so you can compare them with the tags in the viewer. It is off by default.
- **Configurable images:** number of series and images, matrix from 64 to 4096, and 8 to 16 bits stored as the modality allows.
- **Transfer syntaxes:** Implicit VR LE, Explicit VR LE, JPEG Lossless (Process 14 SV1).
- **Full response logging:** status code, state, description, Error Comment, Offending Element and time for every image, including warnings and vendor-specific codes.
- **Save to disk:** instead of, or in addition to, sending, as a DICOM File-set with a DICOMDIR that viewers can open.
- **Connectivity:** Ping, DICOM Echo, TLS, and saved destination profiles.
- **Test suite:** run tests against a PACS from the **Tests** tab or the command line (`DCST4.Cli.exe`). The first test is C-ECHO, and each run is saved as JSON and CSV for comparison over time. See [Testing a PACS](#testing-a-pacs).
- **Protocol timeline:** each test records the steps of the association, from A-ASSOCIATE to A-RELEASE or A-ABORT, with every DIMSE request and response and its raw status.
- **Reproducible test datasets:** the same seed gives the same files, from the command line. Single-frame and multi-frame images, Enhanced CT and MR, SR, Key Object Selection, Presentation State, Encapsulated PDF, Segmentation and RT Structure Set, in compressed variants and with character set and edge-case variants. Each comes with a manifest of hashes. See [Test datasets](#test-datasets).


## Install

You can choose between the installer and the portable version, and neither needs administrator rights.

### Installer

1. Download `DCST4.msi` from the [latest release](../../releases/latest).
2. Double-click the file to install the program.
3. Start **DICOM C-Store Tester** from the Start menu.

The installer installs for the current user only, by default to `%LOCALAPPDATA%\DICOM C-Store Tester`. Uninstalling leaves your settings and logs in `%LOCALAPPDATA%\DCST4`.

### Portable

1. Download the ZIP file from the [latest release](../../releases/latest).
2. Extract the files to a folder of your choice.
3. Run `DCST4.exe`. The command line version `DCST4.Cli.exe` is in the same folder. Keep both next to the `libs` folder.

### Microsoft SmartScreen warning

The program is not digitally signed, so you may see a **Windows protected your PC** warning the first time you run it. To continue, click **More info** and then **Run anyway**.



## Usage

1. Enter the server's AE title, host and port. Try **Ping** and **Dicom Echo**.
2. On the **Exam** tab, select modality and exam type, and how many images you want. 1 series with 1 image gives a single test image.
3. Optionally, enter your own values on the **Identifiers** tab by unticking **Auto**.
4. Click **Generate & Send** and follow each image in **Results**.
5. Look the exam up in the PACS viewer. To compare the image with the tags, turn on burned-in text first.

F1 in the program opens the help.

### When Auto values change

Auto values are drawn when the program starts and when you click **New values**. They stay on screen after a run, so the exam can be looked up in PACS.

Running again without **New values** uses the same patient, accession number and Study UID, so the images arrive as a new series in the same study, also if you changed modality or exam type. Series numbers start at 1 again, and an Auto Study Date/Time is the time of each run. To draw new values after every run instead, tick **Draw new Auto values after each run** under **File > Settings**, on the **Identifiers** tab.

### Burned-in text

By default the images have no burned-in patient details. Under **File > Settings**, on the **Burned-in text** tab, you can turn on a black text box in the upper left corner of each image and tick which tags it shows: Patient's Name, Patient ID, Patient's Birth Date, Patient's Sex, Accession Number, Study ID, Study Date, Study Time, Study Description, Series Description, Body Part Examined, Modality, Series Number, Instance Number, Content Date, Content Time, Institution Name, Station Name and Referring Physician's Name.

The values are read from the image's own tags and shown as they are stored, so a date reads `20261106`. Burned In Annotation is `YES` when text is drawn and `NO` otherwise. The diagonal TEST IMAGE watermark is always there.

### Character sets

The **Character set** list under **Exam** offers ISO_IR 100 (the default), ISO_IR 192 and **Empty value (Basic ASCII / ISO 646)**. Empty value writes Specific Character Set with zero length, which means the default repertoire. Random names and common additional tags then lose their diacritics, so `Åström` becomes `Astrom`, and a manual value with any other character than Basic ASCII stops the run with a message. A strict validator may object to the empty tag, since this Type 1C attribute should have a value when it is present.

### Additional tags

Under **File > Settings**, on the **Additional tags** tab, you can add tags on top of the fixed fields. Each one gets a row on the **Identifiers** tab, where unticking **Auto** lets you type a value for the current session.

![The Additional tags tab in Settings](screenshots/settings-tags.png)

The same tags as rows on the **Identifiers** tab, below the fixed fields. Here Accession No has a typed value and the rest are on Auto:

![The Identifiers tab with added tags](screenshots/identifiers.png)

- **Common tags:** tick any of Operators' Name, Performing Physician's Name, Name of Physician(s) Reading Study, Institutional Department Name, Issuer of Patient ID, Patient Comments, Additional Patient History, Admission ID, Performed Procedure Step ID and Performed Procedure Step Description. Auto generates the value.
- **Custom tags:** enter a tag as `(0008,1070)`, `00081070` or a keyword such as `OperatorsName`. The name and VR fill in from the DICOM dictionary. Auto uses the default value you enter, and an empty default sends the tag empty. Separate several values with a backslash where the tag allows it.
- **Private tags:** a tag with an odd group needs a private creator and a VR. The creator is written to the block of the number you entered, so `(0011,2001)` puts the creator in `(0011,0020)`. A receiver using Implicit VR sees these values as UN unless it knows the creator.

A custom tag is written after the program's own tags, so it can replace a generated value such as Manufacturer. Some tags cannot be added, because they would break the image or remove its marking as a test image: groups 0000 to 0007, group lengths, groups 0028 and 7FE0, SOP Class, Modality, Specific Character Set and Image Comments. Tags that already have a field on the **Identifiers** tab are set there. Sequences, binary VRs and AT are not supported, since their values cannot be typed.

### Version check

When the program starts, it asks GitHub for the latest release (`api.github.com`). If there is a new version, a window opens with a link to it and the release notes, which are collapsed until you open them. You only see this window when a new version was found: if GitHub cannot be reached, for example on a closed network, the program says nothing and carries on, and the failure is written to the log.

To turn this off, untick **Show a window at startup when a new version is available** under **File > Settings**, on the **Updates** tab. Nothing is then sent at startup.

Opening **Help > About** also asks GitHub and shows the result in that window, including if the check failed. Nothing is sent except an ordinary web request with the program's version as its User-Agent, and nothing is downloaded or installed.

### Transfer syntax behaviour

fo-dicom always proposes Implicit VR in a presentation context of its own, next to the selected syntax, and would silently convert to it if the selected one was not accepted. A test of JPEG Lossless could then pass against a PACS that cannot take it.

So by default, **the run stops with an error and sends nothing if the server does not accept the selected syntax.** Tick **Allow fallback to Implicit VR** to behave like a real modality instead. The run then finishes with a warning, and the **Syntax used** column shows what was really sent.

### Reading the results

| State   | Meaning                                                    | Examples                     |
| ------- | ---------------------------------------------------------- | ---------------------------- |
| Success | Stored                                                     | `0x0000`                     |
| Warning | Stored, but the server changed or dropped something        | `0xB000`, `0xB006`, `0xB007` |
| Failure | Not stored. The run continues with the next image          | `0xA7xx`, `0xA9xx`, `0xCxxx` |
| NotSent | No response, for example association rejected or timed out |                              |

### Saving to disk

The selected folder becomes a DICOM File-set:

```
<folder>\DICOMDIR
<folder>\DICOM\EX000001\S001\IM0001
```

DICOM limits each part of a file name to 8 upper-case characters with no extension, so the folders do not show patient details. The **Saved to** column shows where each image went.

Each run gets a new `EX` folder, so nothing is overwritten. Saving again to the same folder adds to the same DICOMDIR, which is rebuilt from all files in the File-set when each run ends, also if the run was cancelled or sending failed. Each record states its own Specific Character Set, so exams in ISO_IR 100 and ISO_IR 192 can share a DICOMDIR. A record for an exam with an empty Specific Character Set leaves the tag out.

## Testing a PACS

A test run is a set of tests against one destination. The result of each test is one of these, never just pass or fail:

| Result             | Meaning                                                                               |
| ------------------ | ------------------------------------------------------------------------------------- |
| `PASS`             | The system did what the test expects.                                                 |
| `FAIL`             | It answered, refused or broke off in a way the test does not accept.                  |
| `NOT_SUPPORTED`    | It said it does not support this, for example by not accepting the SOP class.         |
| `EXPECTED_FAILURE` | It refused something it is supposed to refuse.                                        |
| `TIMEOUT`          | No connection, association or response within the timeout.                            |
| `INDETERMINATE`    | The test could not reach a verdict, for example because the run was cancelled.        |
| `TOOL_ERROR`       | A bug or local problem in DCST4, such as a missing client certificate. Not the PACS's fault. |

### In the program

Open the **Tests** tab below the exam settings, tick the tests and click **Run tests**. They run against the destination entered under Server. Select a row to see its explanation and protocol timeline. **Cancel** stops the run, and what was finished is still saved.

### On the command line

**Save profile as JSON...** on the Tests tab writes the destination to a file, or you can write one yourself. Only `endpoint` is required:

```json
{
  "name": "Test PACS",
  "endpoint": { "host": "pacs.example.org", "port": 104, "callingAe": "DCST4", "calledAe": "PACS" },
  "network": { "connectionTimeoutSeconds": 10, "associationTimeoutSeconds": 10, "responseTimeoutSeconds": 60 },
  "localScpAe": "DCST4",
  "localScpPort": 11113,
  "uidRoot": "2.25"
}
```

For TLS, add `"useTls": true` to `endpoint`, and optionally `"tlsIgnoreCertErrors": true` and `"tlsClientCertThumbprint"`.

```powershell
DCST4.Cli.exe run --profile target.json --tests c-echo --out C:\Temp\results
```

`--tests` takes test ids, categories (`Capability`, `Integrity`, `Limits`, `Performance`) or `all`, separated by commas. `--log-dir` chooses another folder for the log. Ctrl+C cancels the run and saves what was finished. A second Ctrl+C ends the program at once. Cancelling while a connection is being opened can take as long as the connection timeout.

| Exit code | Meaning                                                    |
| --------- | ---------------------------------------------------------- |
| 0         | No `FAIL` or `TOOL_ERROR`                                  |
| 1         | At least one `FAIL` or `TOOL_ERROR`                        |
| 2         | Bad arguments, an unusable profile or results that could not be saved |
| 130       | Cancelled                                                  |

### Result files

Each run writes `DCST4-run-<UTC time>.json` with full detail and a `.csv` with one row per result. Both are UTF-8 and independent of the computer's language settings, and they hold a schema version, the tool version, the destination and the start and end time. The client certificate thumbprint is never written to them.

Two identical runs differ only in the times and durations, from the program or the command line alike.

## Test datasets

The test suite works with datasets that can be generated again, unlike Generate & Send, where every run gets new UIDs and the current date. `DCST4.Cli.exe generate` writes a dataset as a DICOM File-set with a DICOMDIR and a `manifest.json`:

```powershell
DCST4.Cli.exe list-datasets
DCST4.Cli.exe generate --dataset enhanced-ct --out C:\Temp\enhanced --seed 42 --frames 20
```

| Option                                                                | Meaning                                                                                                           |
| --------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| `--dataset`                                                           | A profile from `list-datasets`                                                                                    |
| `--seed`                                                              | Everything that varies follows from this. Default 1                                                               |
| `--uid-root`                                                          | UIDs start with this root. Without it they are `2.25.` and a value hashed from the seed and counters              |
| `--patient-naming`                                                    | `RegistrySwedishName` (default) or `MarkedTestName`, which gives `DCST4^TEST^<number>`                            |
| `--patients` `--studies` `--series` `--instances`                     | How many. Default 1 of each                                                                                       |
| `--rows` `--columns` `--bits` `--frames`                              | The image. The profile's own values if left out                                                                   |
| `--transfer-syntax`                                                   | `explicit` (default), `implicit`, or a name from `list-codecs`, or a Transfer Syntax UID                          |
| `--charset`                                                           | `default`, or `empty`, `latin1` or `utf8` with å, ä, ö, Å, Ä and Ö in the patient name and the descriptions       |
| `--edge`                                                              | `private`, `long` or `nested`, separated by commas: private tags, values at maximum length, sequences three deep  |

Assume that a PACS cannot delete what it receives, and keep the volumes small. `tomosynthesis-large`, about 700 MB per object, only runs when asked for by name.

**Patient IDs** come only from Skatteverket's list of test personnummer, whatever the naming. The name can be a Swedish name or `DCST4^TEST^<number>`, chosen with `--patient-naming` or on the Identifiers tab of **File > Settings**. That setting does not affect Generate & Send, and it has no visible effect in the program until a test on the Tests tab uses datasets.

**What is guaranteed:** the same seed, parameters, personnummer list and reference date give byte-identical files, within one DCST4 version, one fo-dicom and PureCodecs version, and the same Windows GDI+ and Consolas rendering. On another computer the UIDs, names and all non-pixel attributes still match, and the pixels match only if the rendering does. `manifest.json` records these versions. The reference date is fixed, so the same request gives the same result on any day.

**The manifest** holds, for each object, the SOP Class and Instance UID, the transfer syntax, a SHA-256 of the whole file, a SHA-256 of each frame and a normalized hash of the other attributes. The frame hashes are taken from the raw frames before any encoding, so that no decoder is involved. The normalized hash leaves out the file meta group, Specific Character Set, Pixel Data, VRs and private sequences, and compares text as Unicode, so it survives a change of transfer syntax or character set. Private sequences are left out because Implicit VR returns them as unreadable bytes. The whole-file hash still covers them. An object that refers to an image, such as a Key Object Selection, gets one small CT image of its own first, marked `source-image` in the manifest.

**Compressed variants** are tried per bit depth, since PureCodecs is pre-1.0. `list-codecs` shows what works on this computer. What cannot be produced is reported as unavailable in the output and the manifest, and is not an error. JPEG Baseline is 8-bit only and JPEG Extended at most 12-bit. A lossy variant has Lossy Image Compression `01` with ratio and method, and Image Type `DERIVED`.

## Generated tags

| Group      | Values                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| All images | Specific Character Set, SOP Class and Instance UID, Patient (Name, ID, Birth Date, Sex, Age), Study (UID, Date, Time, ID, Accession Number, Referring Physician, Description), Series (Modality, UID, Number, Date, Time, Description, Body Part, Protocol), Equipment (Manufacturer `DCST4`, Model, Institution, Station, Serial Number, Software Version), Image (Instance Number, Content and Acquisition Date and Time, Image Type, Image Comments, Burned In Annotation `YES` or `NO`, Lossy Image Compression `00`, Window Center and Width) |
| CT         | Frame of Reference, Patient Position, slice geometry, KVP, Tube Current, Exposure Time, Convolution Kernel, Rescale −1024 HU                                                                                                                                                                                                                                                                                                                                                                                                               |
| MR         | Frame of Reference, Patient Position, slice geometry, Scanning Sequence, Sequence Variant, Scan Options, TR, TE, Echo Train Length, Flip Angle, Field Strength, Imaging Frequency                                                                                                                                                                                                                                                                                                                                                          |
| CR         | View Position, Imager Pixel Spacing, Patient Orientation, KVP, Exposure Time, Tube Current                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| DX         | As CR, plus Presentation Intent, Presentation LUT Shape, Pixel Intensity Relationship, Rescale, Detector Type, Image Laterality                                                                                                                                                                                                                                                                                                                                                                                                            |
| US         | Single-frame grayscale B-mode (MONOCHROME2, 8 bits): Image Type with application and `0001`, Transducer Data and Type, Depth of Scan Field, Mechanical and Thermal Index, Sequence of Ultrasound Regions with calibration in cm                                                                                                                                                                                                                                                                                                            |
| XA, RF     | Single frame: Image Type with `SINGLE PLANE`, Imager Pixel Spacing, Pixel Intensity Relationship, Radiation Setting, KVP, Tube Current, Exposure Time, Exposure, Positioner Primary and Secondary Angle |
| MG         | As DX, For Presentation, plus Positioner Type, Organ Exposed, left or right CC or MLO view with Patient Orientation, and Anatomic Region and View Code Sequences |
| PT         | Static stack with Frame of Reference, Patient Position and slice geometry, 16 bits: Units `BQML`, Counts Source, Series Type, Corrected Image, Decay Correction, Number of Slices, Image Index, Frame Reference Time, Actual Frame Duration, F-18 FDG in Radiopharmaceutical Information Sequence, Rescale |
| NM         | Static image, one frame: Image Type with `STATIC` and `EMISSION`, Number of Frames, Frame Increment Pointer, Energy Window and Detector vectors and sequences, Tc-99m in Radiopharmaceutical Information Sequence, Counts Accumulated, Actual Frame Duration, Pixel Spacing |

Window Center and Width are calculated from the stored pixel values, so the image and any burned-in text are readable when it opens.

Image Comments is `https://github.com/droidgren/DCST4`. No standard tag means "project URL". Image Comments is free text that viewers often show, and unlike Retrieve URL (0008,1190) it does not tell any system to fetch something from the address.

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
    Modalities\     ModalityCatalog (Generate & Send) and MultiFrameProfiles (datasets only)
    Objects\        Objects without pixels: SR, KOS, GSPS, PDF, RT Structure Set
    Datasets\       Dataset profiles, TestDatasetGenerator, variants, CompressionCatalog, DatasetManifest
  TestData\         Test personnummer, names, identifiers, UID sources, TestPatientNaming
  Pipeline\         IImageSource, StoreRun (transcode, save, send), RunSummary
  Network\          Ping, C-ECHO, C-STORE, AssociationTimeline, AssociationNegotiation
  Output\           DICOM File-set and DICOMDIR
  Testing\          TargetProfile, ITestCase, TestRunner, TestCatalog, TestResult, ResultFileWriter
    Cases\          The test cases
src\DCST4.App\      WPF app. The Tests tab is ViewModels\TestSuiteViewModel.cs
src\DCST4.Cli\      Command line: DCST4.Cli.exe
tests\DCST4.Tests\  Console test harness and mock Storage SCP
tools\              List refresh script
```

The app and the command line are front-ends on the same engine in `Testing\`. To add a test, implement `ITestCase` in `Testing\Cases\` and add it to `TestCatalog.All`, and it appears in both. Its `Id` must stay the same across versions, because result files are compared by it. Change `TestRunReport.CurrentSchemaVersion` when the result files change.

`AssociationTimeline` is built on `IDicomClient`, because fo-dicom's own event collector is internal. It settles how an association ended only after `SendAsync` is over, since `AssociationReleased` is also raised after an abort. C-ECHO is wired to it so far.

Generation knows nothing about sending. The two meet at `IImageSource`, which supplies images lazily so that a long stack does not sit in memory. That leaves room for:

| Possible addition                                            | Where it plugs in                                                                                  |
| ------------------------------------------------------------ | -------------------------------------------------------------------------------------------------- |
| Sending existing files from a folder, with optional new UIDs | A new `IImageSource`. Sending does not change.                                                     |
| Load testing (send N times, measure throughput)              | Repeat `StoreRun` with a new source. `RunSummary` already has timing, bytes and images per second. |
| Storage Commitment                                           | `RunSummary.Results` keeps each image's SOP Class and Instance UID.                                |
| MPPS                                                         | `ExamRequest` holds the study and series identity. `Network\` gets a new service.                  |

To add a modality, subclass `ModalityProfile` and add it to `ModalityCatalog`. An image type only for datasets goes in `DatasetProfileCatalog` instead, so that it stays out of the Generate & Send list. A non-image object subclasses `ObjectProfile`.

`TestDatasetGenerator` is an `IImageSource`, so its objects go through `StoreRun` like any others. It composes `ExamGenerator` with an `IUidSource`: `GeneratedUidSource` is the default, and `DeterministicUidSource` derives UIDs from the root, seed and counters. Every parameter that changes the data is part of the hashed context, so two variants never share a SOP Instance UID on a PACS. Each object leaves the generator in its final syntax, so a run must use `TestDatasetGenerator.TransferSyntax`. The manifest and the hashes (`ManifestHashes`, `NormalizedAttributes`) are what Phase 4 uses to verify integrity.

Pixel Data of `tomosynthesis-large` comes from `RenderedFramesBuffer`, which draws each frame when it is read and keeps only the latest in memory, so a 700 MB object takes little more than one frame of heap. A dataset has one or two SOP classes, since `StoreService` negotiates from its first window of images.


## Reporting Bugs & Feature Requests

If you encounter any bugs or have an idea for a new feature, please open an issue in the [Issues](../../issues) section of this repository.

- **Bugs:** Please provide steps to reproduce the issue and attach relevant logs if possible.
- **Features:** Describe the desired functionality and how it would improve the tool.
  

## Tests

```powershell
.\tests\run-tests.ps1
```

A console harness in the same style as DMWLT4's, running the real code against a mock Storage SCP. Exit code 0 means all checks passed. The run fails if it changes `%LocalAppData%\DCST4\user.config`.

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
# The datasets folder inside it holds every dataset profile, character set, edge case and codec variant.
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

The dataset profiles added in 1.4.0 were checked once by hand with DCMTK 3.7.1, on the 74 files that `matrix` writes to its `datasets` folder:

- `dcmftest` and `dcmdump` pass with no warnings. The first run found fragments of odd length from the HTJ2K encoder, which are now padded and covered by a check in the tests.
- `dcmdjpeg`, `dcmdjpls` and `dcmdrle` decode the lossless JPEG, JPEG-LS and RLE variants at 8, 12 and 16 bits and RGB to pixels identical to the raw frames. DCMTK has no JPEG 2000 or HTJ2K decoder, so those were checked only with PureCodecs' own decoder.

## Dependencies

| Package                  | Use                                                          | Licence    |
| ------------------------ | ------------------------------------------------------------ | ---------- |
| fo-dicom 5.2.6           | DICOM                                                        | MS-PL      |
| FoDicom.PureCodecs 0.5.1 | JPEG, JPEG-LS, JPEG 2000, HTJ2K and RLE. Managed code, no native DLL. Pre-1.0. | MS-PL      |
| Serilog                  | Logging                                                      | Apache-2.0 |
| System.Text.Json         | Target profiles, result files and manifests. Comes with fo-dicom. | MIT        |

## Files

| What                               | Where                                                          |
| ---------------------------------- | -------------------------------------------------------------- |
| Settings                           | `%LocalAppData%\DCST4\user.config`                             |
| Log, kept for 7 days               | `%LocalAppData%\DCST4\Logs\`, `Cli-*.log` for the command line |
| Test results from the program      | `%LocalAppData%\DCST4\TestRuns\`, or the folder on the Tests tab |
| Test results from the command line | The folder given with `--out`                                  |

## Changelog

### 1.4.0

New:

- Reproducible test datasets: `DCST4.Cli.exe generate` writes a named dataset from a seed as a File-set with a DICOMDIR, and `list-datasets` and `list-codecs` show what is available
- Dataset profiles for CT, MR, CR, DX, US, XA, RF, MG, PT and NM, US multi-frame, Secondary Capture, Enhanced CT, Enhanced MR, a large Breast Tomosynthesis object, Basic Text SR, Key Object Selection, Grayscale Softcopy Presentation State, Encapsulated PDF, Segmentation and RT Structure Set
- Variants: character sets with Swedish letters, private tags, values at maximum length, nested sequences, and JPEG, JPEG-LS, JPEG 2000, HTJ2K and RLE compression. What the encoders cannot produce is reported as unavailable
- A manifest with the SHA-256 of each file and each raw frame, and a normalized hash of the attributes
- Setting for how test-suite datasets name the patient, on the Identifiers tab of File > Settings and as `--patient-naming`. It does not affect Generate & Send

Changed:

- Objects without pixels can pass through a run and into a DICOMDIR, with the record type their SOP class requires

### 1.3.0

New:

- Command line program `DCST4.Cli.exe`, which runs tests from a JSON target profile and returns an exit code that scripts can use
- Tests tab, for selecting and running tests and seeing each result with its protocol timeline
- First test: C-ECHO, with the results `PASS`, `FAIL`, `NOT_SUPPORTED`, `TIMEOUT`, `INDETERMINATE` and `TOOL_ERROR`
- Each test run is saved as JSON and CSV, with a schema version so that later versions can compare them
- Save the destination as a JSON target profile. Saved destination profiles work as before

Changed:

- `tests\run-tests.ps1` now fails, rather than warns, if the run changes `user.config`

### 1.2.2

Fixed:

- A shortcut pinned to Start is now kept when the program is updated with the MSI. The update from an earlier version may still lose the pin once

### 1.2.1

New:

- A window at startup when a new version is available, with a link to it and the release notes, collapsed until opened. A check that fails is only written to the log
- Setting to turn this window off, on the Updates tab under File > Settings. Nothing is then sent to GitHub at startup

### 1.2.0

New:

- Modalities XA, RF, MG, PT and NM, with their own exam types and the tags their IODs require
- Setting to choose which tags are burned into the image, from a checklist under File > Settings
- Empty value (Basic ASCII / ISO 646) in the Character set list. Random names then lose their diacritics, and manual values outside Basic ASCII stop the run with a message

Changed:

- The burned-in text box is now off by default, and can be turned on under File > Settings. Its fixed "TEST IMAGE - NOT FOR DIAGNOSTIC USE" line is gone, and the diagonal watermark stays
- Burned In Annotation is now `NO` when no text is drawn
- Image Comments now holds only the project's address

### 1.1.0

New:

- Additional tags under File > Settings: a checklist of common tags with generated values, and custom tags by number or keyword, including private tags. Each gets a row on the Identifiers tab
- Version check in the About window, which runs when the window opens
- Setting to draw new Auto values after each run

Changed:

- Auto values now stay on screen after a run, so the exam can be looked up in PACS. Running again adds a new series to the same study
- Image Comments now also holds the project's address
- The About box links to the project, and the Help menu uses the short labels Help and About

### 1.0.0

New:

- First version: generated CT, MR, CR, DX and US images with burned-in text, sent with C-STORE or saved to disk as a File-set with a DICOMDIR
- Auto or manual values for all identifiers, with test personnummer from Skatteverket
- Implicit VR LE, Explicit VR LE and JPEG Lossless, with optional fallback
- Per-image response logging, live log and run summary
- Ping, DICOM Echo, TLS and destination profiles from DMWLT4

## License

MIT, see [LICENSE](LICENSE).
