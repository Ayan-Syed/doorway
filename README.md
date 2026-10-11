# Doorway

Doorway is an Android app for indoor navigation on UCF's campus. It uses augmented reality. It finds where you are
inside a building from AR tracking, step counting, wall matching and room-number signs it reads with the camera.
Then it routes you to a room and draws the route on the floor in AR, with voice guidance as you walk. 

## Tech stack

### App

| Layer | Technology |
|---|---|
| Engine | [Unity](https://unity.com/) 6000.3.25f1 (Unity 6.3), Built-in Render Pipeline |
| Language | C# |
| AR | [AR Foundation](https://docs.unity3d.com/Packages/com.unity.xr.arfoundation@6.3/) 6.3.5, [ARCore XR Plugin](https://docs.unity3d.com/Packages/com.unity.xr.arcore@6.3/) 6.3.5 (planes, environment depth / occlusion, CPU camera images) |
| XR plumbing | XR Plug-in Management 4.6.1, XR Core Utilities 2.6.0, XR Simulation (in-editor testing) |
| Input | Unity Input System 1.20.0 |
| UI | uGUI 2.0.0, custom shaders (route ribbon, map walls, highlights) |
| Platform | Android, API 25+, ARM64, IL2CPP, OpenGL ES 3 |
| Text recognition | [Google ML Kit Text Recognition v2](https://developers.google.com/ml-kit/vision/text-recognition/v2) 16.0.1, bundled Latin model (offline), in a Java Android library plug-in |
| Speech to text | Android `SpeechRecognizer`, called through JNI |
| Text to speech | Android `TextToSpeech`, called through JNI |
| Motion sensors | Android step detector, linear acceleration and rotation vector sensors (Java plug-in) |
| Location | Android GPS (Unity `Input.location`) |
| Haptics | Android `VibrationEffect`, called through JNI |
| Fonts and icons | Google Sans, Inter, Material Icons |

### Data pipelines (`Tools/`)

The floor plans, walkable grids, walls, places and campus map in `Assets/Navigation/Data` come from Python scripts.

| Purpose | Technology |
|---|---|
| Language | Python 3.12 |
| Image and geometry processing | NumPy, SciPy (`ndimage`, sparse graphs, optimisation), OpenCV (headless), Pillow, Shapely |
| Triangulation | mapbox_earcut |
| Campus map data | [OpenStreetMap](https://www.openstreetmap.org/) via the [Overpass API](https://overpass-api.de/) |
| Floor plan source data | [Knight Hacks](https://knighthacks.org/) traced floor plans and room data (MIT, see `FloorPlans/LICENSE-knighthacks-forge.txt`), plus archived UCF College of Business floor plans |

Exact versions are pinned in [Tools/requirements.txt](Tools/requirements.txt). With those versions, the scripts
rebuild every committed data file byte for byte.

### Voice intent model (`Tools/voice-intent/`)

| Purpose | Technology |
|---|---|
| Base model | [google/functiongemma-270m-it](https://huggingface.co/google/functiongemma-270m-it) (FunctionGemma 270M) |
| Training | PyTorch, Hugging Face Transformers, Accelerate |
| Datasets, model hosting, training jobs | Hugging Face Hub, Hugging Face Jobs (`huggingface_hub`) |
| Synthetic paraphrases | Hugging Face Inference Providers (e.g. Qwen2.5-72B-Instruct) |
| Script runner | [uv](https://docs.astral.sh/uv/) (inline script dependencies) |
| Parser test harness | .NET 8 console app that compiles the app's C# voice parser outside Unity |

The app's rule-based `VoiceParser` always works offline. The fine-tuned model can take over the wordings the rules
miss.

### Analytics (`Tools/databricks/`)

| Purpose | Technology |
|---|---|
| Platform | [Databricks](https://www.databricks.com/) (Free Edition works), Unity Catalog volumes, Databricks Jobs |
| Upload | Databricks SDK for Python 0.145.0, Databricks CLI |
| Pulling logs off phones | `adb` (Android Debug Bridge) |
| Notebook analysis | pandas, Matplotlib |

Walk logs and positioning evaluations become tables you can query. See
[Tools/databricks/README.md](Tools/databricks/README.md).

### Testing and CI

| Purpose | Technology |
|---|---|
| App tests | Unity Test Framework 1.6.0 (NUnit), Edit Mode tests in `Assets/Tests/EditMode` |
| Data and routing tests | pytest 8.4.2, in `Tools/tests` |
| Continuous integration | GitHub Actions |
| Unity in CI | [GameCI](https://game.ci/) `unity-test-runner` and `unity-builder` (tests, then a test APK) |
| Compile check without the editor | `Tools/dev/check.sh` (Unity's bundled Roslyn compiler) |

## Repository layout

```
Assets/
  Navigation/Scripts/   the app: localizer, router, navigator, AR guidance, maps, voice, UI
  Navigation/Data/      generated floor and campus data (built by Tools/)
  Navigation/Shaders/   route ribbon, map walls and highlight shaders
  Editor/               scene builder, APK build, replay evaluator, synthetic walks
  Plugins/Android/      Java plug-in: ML Kit text reader and step sensor
  Tests/EditMode/       Unity Edit Mode tests, plus recorded field walks
  XR/                   ARCore and XR Simulation settings
FloorPlans/             source floor plans and Knight Hacks data
Tools/
  ba1-floor1/, hec-floor1/, campus/   data pipelines
  voice-intent/         FunctionGemma dataset, training and evaluation
  databricks/           walk-log ingestion and analytics
  eval/                 positioning evaluation report
  tests/                pytest suite
  dev/check.sh          compile check outside Unity
.github/workflows/      CI: Unity tests and APK, data and routing tests
```

## Getting started

### Build the app

1. Install **Unity 6000.3.25f1** with **Android Build Support** (OpenJDK, Android SDK and NDK) through Unity Hub.
2. Open the project. Unity resolves the packages from `Packages/manifest.json`.
3. Build with the `BuildNavigateScene.BuildApk` editor method (the one CI uses). Or switch the platform to
   Android and build from *File → Build Profiles*. You need an ARCore-supported Android phone.

### Run the tests

```sh
# Data and routing tests
pip install -r Tools/requirements.txt
python -m pytest Tools/tests -q

# Unity Edit Mode tests: Window → General → Test Runner → EditMode → Run All
```

### Rebuild the data

Run the floor pipelines first, then the campus map:

```sh
python Tools/ba1-floor1/make_data.py
python Tools/hec-floor1/make_data.py
python Tools/campus/campus.py
```

## Credits

- Floor plan traces and room data: [Knight Hacks](https://knighthacks.org/) (MIT License).
- Map data © [OpenStreetMap](https://www.openstreetmap.org/copyright) contributors (ODbL).
- Fonts: Google Sans and Inter (SIL Open Font License), Material Icons (Apache 2.0). The licence files are in
  `Assets/Navigation/Fonts/`.
