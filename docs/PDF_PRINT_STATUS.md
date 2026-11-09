# Card PDF stage (partial native printing port)

This PDF exporter is independent .NET code using PDFsharp (MIT), not a
Python process, remote service or print-to-PDF driver. It creates physical
A4/A3 pages from the existing PrintLayouts placements, including duplex
sheet order, original username uniqueness checks and atomic output files.
Credentials are PDF text that can be selected; no passwords are logged.

It currently draws a simplified **glossy-red** vector voucher design.
Original Python custom background images, draggable positions, QR codes,
font/color/layer variants, cut marks, native Windows spooler/print-driver
verification and physical test sheets are **not yet implemented**.
The original Python repository and database remain untouched, and passing
a synthetic PDF test does not mean complete visual parity.

PDFsharp's installed-Windows Arial lookup reads the system font on the
target PC. No Windows font files are redistributed via the repository.

Do not release until the original card-design/printing capabilities and
real print-driver acceptance are complete.
