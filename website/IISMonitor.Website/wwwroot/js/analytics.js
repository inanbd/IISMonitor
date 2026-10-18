// Google Analytics (gtag.js). The page includes this only when Site:GoogleAnalyticsId is set,
// outside Development; the measurement ID comes from the script tag's data-measurement-id.
window.dataLayer = window.dataLayer || [];
function gtag() { dataLayer.push(arguments); }
gtag("js", new Date());
gtag("config", document.currentScript.dataset.measurementId);
