# Regression checks

Run `./test.ps1` from the repository root. It builds the application and separate
test programs, then checks:

- Gamut geometry, aspect ratios, histogram mass, multimodal separation, PCA
  orthogonality, model round trips, invalid-model rejection, and binary export.
- Continuous HSV cluster geometry, permutation/symmetry checks, exact mosaic
  coverage, cluster model round trips and GPU/CPU population/spread/inference agreement.
- Delayed/noisy nonlinear camera feedback, plateau detection, discrete final
  convergence, timing, and response settling without opening a camera.
- GPU/CPU prediction agreement, shader compilation, single-threaded device
  compatibility, complete graphics-state restoration, and uniform HDR gain.

Outputs go under ignored `build/` directories. The production GUI does not expose
test commands or automated live DWM toggle/injection commands. No test script
attaches to the live compositor or opens the camera.

`./tests/test-overlay.ps1` separately checks the private leaf-function register
contract. It resolves the installed DWM DLL's matching Microsoft symbols (which
may require network access), maps that DLL into its own test process, reproduces
the unsafe replacement's access violation, and verifies the preserving wrapper
under 100 concurrent toggle cycles. This does not demonstrate live game stability.

The managed harness compiles the application sources into a separate executable
so internal numerical routines can be checked without publishing a testing API
or adding runtime dependencies to the application.
