// Downloads a generated file (reports) in the browser.
export function downloadFile(fileName, contentType, base64) {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
}

// Shares a file to WhatsApp: the share sheet when the browser can share files (phones), otherwise a download
// plus WhatsApp Web with the text, so the admin attaches the file there.
export async function shareToWhatsApp(fileName, contentType, base64, message) {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const file = new File([bytes], fileName, { type: contentType });
    if (navigator.canShare && navigator.canShare({ files: [file] })) {
        try {
            await navigator.share({ files: [file], text: message });
            return;
        } catch (e) {
            if (e && e.name === 'AbortError') return;
        }
    }
    downloadFile(fileName, contentType, base64);
    window.open('https://wa.me/?text=' + encodeURIComponent(message), '_blank', 'noopener');
}
