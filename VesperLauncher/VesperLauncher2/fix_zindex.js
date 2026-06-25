const fs = require('fs');
let css = fs.readFileSync('UserInterface/src/styles.css', 'utf8');

// Update z-index for pseudo elements
css = css.replace(/z-index:\s*5;/g, 'z-index: 4;');
css = css.replace(/z-index:\s*4;/g, 'z-index: 2;');

// Update left-liquid-glass-content z-index
css = css.replace(/\.left-liquid-glass-content\s*\{\s*position:\s*relative;\s*z-index:\s*2;/g, '.left-liquid-glass-content { position: relative; z-index: 3;');

fs.writeFileSync('UserInterface/src/styles.css', css, 'utf8');
