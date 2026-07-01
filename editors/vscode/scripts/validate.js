'use strict';

const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
for (const relative of [
    'package.json',
    'language-configuration.json',
    'syntaxes/ruslang.tmLanguage.json',
    'snippets/ruslang.json'
]) {
    JSON.parse(fs.readFileSync(path.join(root, relative), 'utf8'));
    process.stdout.write(`исправен: ${relative}\n`);
}
