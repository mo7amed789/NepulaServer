// Replace the existing PDF button handler with the real engine.
async function exportPdf() {
  if (!conversation) return;

  const btn = $("pdfBtn");
  const original = btn.querySelector("b")?.textContent || "Export as PDF";

  try {
    btn.disabled = true;
    if (btn.querySelector("b")) btn.querySelector("b").textContent = "Preparing PDF...";
    setStatus("Preparing PDF...");

    if (!window.ConvoPDFEngine) {
      throw new Error("PDF engine is not loaded.");
    }

    const result = await window.ConvoPDFEngine.exportPdf(conversation);

    setStatus("PDF ready");
    toast("Print dialog opened — choose Save as PDF");
    console.log("ConvoPDF PDF:", result);
  } catch (error) {
    console.error("ConvoPDF PDF export:", error);
    setStatus("PDF failed", true);
    toast(error?.message || "PDF export failed.", true);
  } finally {
    btn.disabled = false;
    if (btn.querySelector("b")) btn.querySelector("b").textContent = original;
  }
}
