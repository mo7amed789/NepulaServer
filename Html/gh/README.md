# ConvoPDF PDF Engine

This engine converts the extracted conversation JSON into a print-ready A4 document.

## Important

This version uses Chromium's native print pipeline instead of a third-party PDF
library. That gives reliable Arabic/RTL shaping and keeps the conversation local.

The user clicks:

Export as PDF
    ↓
ConvoPDFEngine.exportPdf(conversation)
    ↓
Print-ready A4 document
    ↓
Chrome print dialog
    ↓
Save as PDF

## Integration

1. Add `pdf-engine.js` to the extension.
2. Load it before `popup.js` in `popup.html`.
3. Replace the old `exportPdf()` function in `popup.js` with the version in
   `popup-pdf-handler.js`.

## popup.html

Use:

<script src="pdf-engine.js"></script>
<script src="popup.js"></script>

The extension does not upload conversation content to a server.
