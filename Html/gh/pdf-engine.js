/* ConvoPDF PDF Engine
 * Client-side HTML -> printable PDF engine.
 *
 * Strategy:
 * 1. Build a dedicated print document from the normalized conversation JSON.
 * 2. Use the browser print pipeline so Arabic/RTL text is rendered by Chromium's
 *    native shaping engine.
 * 3. Trigger print-to-PDF from the extension. No conversation data leaves the browser.
 *
 * This is intentionally dependency-free.
 */

(() => {
  "use strict";

  const ENGINE_VERSION = "1.0.0";

  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#039;");
  }

  function safeFileName(name) {
    return (name || "conversation")
      .replace(/[<>:"/\\|?*\x00-\x1F]/g, "")
      .replace(/\s+/g, "-")
      .slice(0, 90) || "conversation";
  }

  function formatDate(iso) {
    try {
      return new Intl.DateTimeFormat("en-GB", {
        dateStyle: "medium",
        timeStyle: "short"
      }).format(new Date(iso));
    } catch {
      return iso || "";
    }
  }

  function detectDirection(text) {
    const arabic = (String(text).match(/[\u0600-\u06FF]/g) || []).length;
    const latin = (String(text).match(/[A-Za-z]/g) || []).length;
    return arabic > latin ? "rtl" : "ltr";
  }

  function messageHtml(message, index) {
    const role = message.role === "assistant" ? "assistant" : "user";
    const label = role === "assistant" ? "AI Assistant" : "You";
    const direction = detectDirection(message.content);

    return `
      <article class="message ${role}" dir="${direction}">
        <div class="message-head">
          <span class="avatar ${role}">${role === "assistant" ? "✦" : "U"}</span>
          <span class="message-role">${escapeHtml(label)}</span>
          <span class="message-number">#${index + 1}</span>
        </div>
        <div class="message-body">${escapeHtml(message.content)
          .replace(/\n/g, "<br>")}</div>
      </article>
    `;
  }

  function buildDocument(conversation) {
    const messages = Array.isArray(conversation?.messages)
      ? conversation.messages
      : [];

    const title = conversation?.title || "Untitled Conversation";
    const provider = conversation?.provider || "AI";
    const extractedAt = conversation?.extractedAt || new Date().toISOString();

    const body = messages.map(messageHtml).join("");

    return `<!doctype html>
<html lang="auto">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(title)}</title>
<style>
  @page {
    size: A4;
    margin: 18mm 15mm 18mm 15mm;
  }

  * { box-sizing: border-box; }

  html, body {
    margin: 0;
    padding: 0;
    background: #fff;
    color: #151821;
    font-family:
      "Noto Sans Arabic",
      "Segoe UI",
      "Tahoma",
      Arial,
      sans-serif;
    -webkit-print-color-adjust: exact;
    print-color-adjust: exact;
  }

  body {
    font-size: 11pt;
    line-height: 1.75;
  }

  .document {
    width: 100%;
  }

  .cover {
    border-bottom: 2px solid #171a22;
    padding-bottom: 20px;
    margin-bottom: 22px;
  }

  .brand {
    font-size: 10pt;
    font-weight: 800;
    letter-spacing: .08em;
    text-transform: uppercase;
    color: #6d5ce7;
    margin-bottom: 9px;
  }

  h1 {
    margin: 0 0 8px;
    font-size: 23pt;
    line-height: 1.25;
    font-weight: 800;
    overflow-wrap: anywhere;
  }

  .meta {
    color: #697181;
    font-size: 9pt;
  }

  .summary {
    display: flex;
    gap: 9px;
    flex-wrap: wrap;
    margin-top: 16px;
  }

  .chip {
    border: 1px solid #dfe3ea;
    border-radius: 999px;
    padding: 4px 9px;
    color: #4f5664;
    font-size: 8.5pt;
  }

  .messages {
    display: block;
  }

  .message {
    break-inside: avoid;
    page-break-inside: avoid;
    margin: 0 0 17px;
    border: 1px solid #e1e4ea;
    border-radius: 10px;
    overflow: hidden;
  }

  .message-head {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 35px;
    padding: 7px 10px;
    background: #f5f6f8;
    border-bottom: 1px solid #e1e4ea;
  }

  .avatar {
    width: 24px;
    height: 24px;
    display: inline-grid;
    place-items: center;
    border-radius: 7px;
    font-size: 9pt;
    font-weight: 800;
    flex: 0 0 auto;
  }

  .avatar.user {
    background: #e9e7ff;
    color: #5b4bd7;
  }

  .avatar.assistant {
    background: #e5f8f0;
    color: #147a57;
  }

  .message-role {
    font-size: 9pt;
    font-weight: 800;
  }

  .message-number {
    margin-left: auto;
    color: #8b93a1;
    font-size: 8pt;
  }

  .message-body {
    padding: 12px 13px 14px;
    white-space: normal;
    overflow-wrap: anywhere;
    word-break: normal;
  }

  .message[dir="rtl"] .message-head {
    direction: rtl;
  }

  .message[dir="rtl"] .message-number {
    margin-left: 0;
    margin-right: auto;
  }

  .message[dir="rtl"] .message-body {
    text-align: right;
  }

  .message[dir="ltr"] .message-body {
    text-align: left;
  }

  .empty {
    padding: 25px;
    text-align: center;
    color: #697181;
  }

  .footer {
    margin-top: 24px;
    padding-top: 9px;
    border-top: 1px solid #e1e4ea;
    color: #8a919e;
    font-size: 8pt;
    text-align: center;
  }

  @media print {
    .cover {
      break-after: avoid;
    }
    .message {
      break-inside: avoid;
    }
  }
</style>
</head>
<body>
  <main class="document">
    <section class="cover">
      <div class="brand">ConvoPDF</div>
      <h1>${escapeHtml(title)}</h1>
      <div class="meta">
        ${escapeHtml(provider)} · Extracted ${escapeHtml(formatDate(extractedAt))}
      </div>

      <div class="summary">
        <span class="chip">${messages.length} messages</span>
        <span class="chip">${messages.filter(x => x.role === "user").length} user</span>
        <span class="chip">${messages.filter(x => x.role === "assistant").length} AI</span>
      </div>
    </section>

    <section class="messages">
      ${
        messages.length
          ? body
          : '<div class="empty">No messages were extracted.</div>'
      }
    </section>

    <div class="footer">
      Generated locally by ConvoPDF · Engine ${ENGINE_VERSION}
    </div>
  </main>
</body>
</html>`;
  }

  function createPrintWindow(conversation) {
    const html = buildDocument(conversation);
    const printWindow = window.open("", "_blank", "width=900,height=1000");

    if (!printWindow) {
      throw new Error(
        "The browser blocked the print window. Allow pop-ups for this extension."
      );
    }

    printWindow.document.open();
    printWindow.document.write(html);
    printWindow.document.close();

    return printWindow;
  }

  async function exportPdf(conversation) {
    if (!conversation || !Array.isArray(conversation.messages)) {
      throw new Error("Invalid conversation data.");
    }

    const printWindow = createPrintWindow(conversation);

    await new Promise(resolve => {
      if (printWindow.document.readyState === "complete") {
        resolve();
        return;
      }

      printWindow.addEventListener("load", resolve, { once: true });
    });

    await new Promise(resolve => setTimeout(resolve, 150));

    printWindow.document.title = safeFileName(conversation.title);
    printWindow.focus();
    printWindow.print();

    return {
      success: true,
      mode: "browser-print",
      fileName: `${safeFileName(conversation.title)}.pdf`
    };
  }

  window.ConvoPDFEngine = {
    version: ENGINE_VERSION,
    buildDocument,
    exportPdf,
    safeFileName
  };
})();
