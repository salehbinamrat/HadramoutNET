# Card PDF stage (partial native printing port)

This PDF exporter is independent .NET code using PDFsharp (MIT), not a
Python process, remote service or print-to-PDF driver. It creates physical
A4/A3 pages from the existing PrintLayouts placements, including duplex
sheet order, original username uniqueness checks and atomic output files.
Credentials are PDF text that can be selected; no passwords are logged.

It currently draws a simplified **glossy-red** vector voucher design.
Original Python custom background images, draggable positions, QR codes,
font/color/layer variants, cut marks, native printer-driver acceptance and physical test sheets are **not yet confirmed**.
The original Python repository and database remain untouched, and passing
a synthetic PDF test does not mean complete visual parity.

PDFsharp's installed-Windows Arial lookup reads the system font on the
target PC. No Windows font files are redistributed via the repository.

Do not release until the original card-design/printing capabilities and
real print-driver acceptance are complete.


## Native Windows printing (initial implementation)

- NativePrintJobContract validates distinct voucher names, A3/A4 dimensions, duplex order and one copy before displaying a driver dialog.
- WPF WindowsVoucherPrinter renders a FixedDocument with actual millimeter dimensions and runs PrintDialog without an external PDF reader. The original customizable artwork is not yet ported.
- The selected printer must validate A3/A4 media size, orientation, duplex and one copy. If its imageable area clips a card, NO job is sent.
- Windows receives one PrintDocument request after confirmation. A possibly accepted job is never retried automatically.
- No physical printer is connected to GitHub Windows CI. Tests assert the pure layout/job contract and CI compiles both WPF architectures. Correct physical cut lines, duplex flipping and clip-free output require device acceptance.
