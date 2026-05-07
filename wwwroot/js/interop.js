window.downloadBase64 = function (base64, filename, mimeType) {
    const byteChars = atob(base64);
    const byteArrays = [];
    for (let i = 0; i < byteChars.length; i += 512) {
        const slice = byteChars.slice(i, i + 512);
        const bytes = new Uint8Array(slice.length);
        for (let j = 0; j < slice.length; j++) bytes[j] = slice.charCodeAt(j);
        byteArrays.push(bytes);
    }
    const blob = new Blob(byteArrays, { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};
