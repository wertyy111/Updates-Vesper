
const fs = require('fs');
let css = fs.readFileSync('styles.css', 'utf8');

// Replace for .left-liquid-glass-content
css = css.replace(/\.left-liquid-glass-content\s*\{[\s\S]*?\}/g, (match) => {
    return match.replace(/pointer-events:\s*none;/, 'pointer-events: auto !important;');
});

// Replace for .main-glass-button
css = css.replace(/\.main-glass-button\s*\{[\s\S]*?\}/g, (match) => {
    return match.replace(/pointer-events:\s*none;/, 'pointer-events: auto !important;');
});

// Just to be absolutely certain, let's append a global override rule for the buttons at the end!
css += '\n\n/* OVERRIDE FOR BROKEN BUTTONS */\n';
css += '.main-glass-button, .left-liquid-glass-content, .left-liquid-glass-button, .account-segmented-toggle button {\n';
css += '    pointer-events: auto !important;\n';
css += '}\n';

fs.writeFileSync('styles.css', css, 'utf8');

