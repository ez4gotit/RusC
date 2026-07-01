'use strict';

const vscode = require('vscode');
const path = require('path');
const fs = require('fs');
const { spawn } = require('child_process');

const languageId = 'ruslang';

function activate(context) {
    const output = vscode.window.createOutputChannel('RusLang');
    const diagnostics = vscode.languages.createDiagnosticCollection(languageId);
    context.subscriptions.push(output, diagnostics);

    const execute = (command, document, extraArguments = [], options = {}) =>
        executeCompiler(command, document, extraArguments, options, output, diagnostics);

    context.subscriptions.push(
        vscode.commands.registerCommand('ruslang.build', async () => {
            const document = await activeRusDocument();
            if (document) {
                await execute('собрать', document, ['--перезаписать'], { announce: true });
            }
        }),
        vscode.commands.registerCommand('ruslang.run', async () => {
            const document = await activeRusDocument();
            if (document) {
                await execute('запустить', document, [], { announce: true });
            }
        }),
        vscode.commands.registerCommand('ruslang.reveal', async () => {
            const document = await activeRusDocument();
            if (!document) {
                return;
            }
            const result = await execute('раскрыть', document, [], { announce: false });
            if (result && result.code === 0) {
                const generated = await vscode.workspace.openTextDocument({
                    language: 'csharp',
                    content: result.stdout
                });
                await vscode.window.showTextDocument(generated, { preview: true });
            }
        }),
        vscode.commands.registerCommand('ruslang.verify', async () => {
            const selection = await vscode.window.showOpenDialog({
                canSelectMany: false,
                filters: { 'Исполняемые файлы': ['exe'] },
                openLabel: 'Проверить'
            });
            if (!selection || selection.length === 0) {
                return;
            }
            await executeStandalone('проверить', selection[0].fsPath, output);
        }),
        vscode.commands.registerCommand('ruslang.health', async () => {
            await executeStandalone('здравие', undefined, output);
        })
    );

    context.subscriptions.push(
        vscode.workspace.onDidSaveTextDocument(document => {
            if (document.languageId === languageId
                && configuration(document.uri).get('checkOnSave', true)) {
                void execute('раскрыть', document, [], { quiet: true });
            }
        }),
        vscode.workspace.onDidOpenTextDocument(document => {
            if (document.languageId === languageId
                && configuration(document.uri).get('checkOnOpen', true)) {
                void execute('раскрыть', document, [], { quiet: true });
            }
        }),
        vscode.workspace.onDidCloseTextDocument(document => diagnostics.delete(document.uri))
    );

    context.subscriptions.push(
        vscode.languages.registerDocumentSymbolProvider(languageId, {
            provideDocumentSymbols: document => provideSymbols(document)
        }),
        vscode.languages.registerHoverProvider(languageId, {
            provideHover: (document, position) => provideHover(document, position)
        })
    );

    const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    status.text = '$(tools) RusLang: собрать';
    status.tooltip = 'Собрать текущую программу RusLang';
    status.command = 'ruslang.build';
    context.subscriptions.push(status);
    const updateStatus = editor => {
        status[editor && editor.document.languageId === languageId ? 'show' : 'hide']();
    };
    updateStatus(vscode.window.activeTextEditor);
    context.subscriptions.push(vscode.window.onDidChangeActiveTextEditor(updateStatus));

    for (const document of vscode.workspace.textDocuments) {
        if (document.languageId === languageId
            && configuration(document.uri).get('checkOnOpen', true)) {
            void execute('раскрыть', document, [], { quiet: true });
        }
    }
}

async function activeRusDocument() {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== languageId) {
        await vscode.window.showWarningMessage('Откройте файл RusLang с расширением .rus.');
        return undefined;
    }
    if (editor.document.isUntitled) {
        await vscode.window.showWarningMessage('Сначала сохраните файл RusLang.');
        return undefined;
    }
    await editor.document.save();
    return editor.document;
}

async function executeCompiler(command, document, extraArguments, options, output, diagnostics) {
    if (document.isUntitled) {
        return undefined;
    }
    const compiler = await resolveCompiler(document.uri);
    if (!compiler) {
        return undefined;
    }

    diagnostics.delete(document.uri);
    const args = [command, document.uri.fsPath, ...extraArguments];
    const result = await runProcess(compiler, args, path.dirname(document.uri.fsPath), output, options.quiet);
    applyDiagnostics(result.stderr, document, diagnostics);

    if (!options.quiet && (options.announce || result.code !== 0)) {
        output.show(true);
    } else if (result.code === 0 && configuration(document.uri).get('showOutputOnSuccess', false)) {
        output.show(true);
    }

    if (options.announce) {
        if (result.code === 0) {
            void vscode.window.showInformationMessage(`RusLang: команда «${command}» выполнена.`);
        } else {
            void vscode.window.showErrorMessage(`RusLang: команда «${command}» завершилась с кодом ${result.code}.`);
        }
    }
    return result;
}

async function executeStandalone(command, file, output) {
    const uri = vscode.window.activeTextEditor?.document.uri
        ?? vscode.workspace.workspaceFolders?.[0]?.uri;
    const compiler = await resolveCompiler(uri);
    if (!compiler) {
        return;
    }
    const args = file ? [command, file] : [command];
    const result = await runProcess(compiler, args, file ? path.dirname(file) : workspaceDirectory(uri), output, false);
    output.show(true);
    if (result.code !== 0) {
        void vscode.window.showErrorMessage(`RusLang: команда «${command}» завершилась с кодом ${result.code}.`);
    }
}

async function resolveCompiler(uri) {
    const configured = configuration(uri).get('compilerPath', '').trim();
    if (configured) {
        const expanded = configured.replace(/\$\{workspaceFolder\}/g, workspaceDirectory(uri));
        const absolute = path.isAbsolute(expanded)
            ? expanded
            : path.resolve(workspaceDirectory(uri), expanded);
        if (fs.existsSync(absolute)) {
            return absolute;
        }
        void vscode.window.showErrorMessage(`Компилятор RusLang не найден: ${absolute}`);
        return undefined;
    }

    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        const candidate = path.join(folder.uri.fsPath, 'artifacts', 'rusc', 'win-x64', 'rusc.exe');
        if (fs.existsSync(candidate)) {
            return candidate;
        }
    }

    if (process.platform === 'win32') {
        const localCandidate = path.resolve(__dirname, '..', '..', 'artifacts', 'rusc', 'win-x64', 'rusc.exe');
        if (fs.existsSync(localCandidate)) {
            return localCandidate;
        }
    }

    return 'rusc';
}

function runProcess(executable, args, cwd, output, quiet) {
    return new Promise(resolve => {
        if (!quiet) {
            output.appendLine(`\n> ${quote(executable)} ${args.map(quote).join(' ')}`);
        }
        let stdout = '';
        let stderr = '';
        let child;
        try {
            child = spawn(executable, args, {
                cwd,
                windowsHide: true,
                shell: false
            });
        } catch (error) {
            void vscode.window.showErrorMessage(`Не удалось запустить rusc: ${error.message}`);
            resolve({ code: -1, stdout, stderr: String(error) });
            return;
        }
        child.stdout.setEncoding('utf8');
        child.stderr.setEncoding('utf8');
        child.stdout.on('data', data => {
            stdout += data;
            if (!quiet) {
                output.append(data);
            }
        });
        child.stderr.on('data', data => {
            stderr += data;
            if (!quiet) {
                output.append(data);
            }
        });
        child.on('error', error => {
            const message = `Не удалось запустить rusc: ${error.message}`;
            stderr += message;
            output.appendLine(message);
            void vscode.window.showErrorMessage(
                'Компилятор rusc не найден. Укажите путь в настройке ruslang.compilerPath.'
            );
        });
        child.on('close', code => resolve({ code: code ?? -1, stdout, stderr }));
    });
}

function applyDiagnostics(stderr, document, collection) {
    const byUri = new Map();
    for (const line of stderr.split(/\r?\n/)) {
        const match = /^(.*)\((\d+),(\d+)\):\s+(ошибка|предупреждение|сведения)\s+([A-Z]+\d+):\s+(.*)$/iu.exec(line);
        if (!match) {
            continue;
        }
        const file = path.resolve(match[1]);
        const uri = vscode.Uri.file(file);
        const range = new vscode.Range(
            Math.max(0, Number(match[2]) - 1),
            Math.max(0, Number(match[3]) - 1),
            Math.max(0, Number(match[2]) - 1),
            Math.max(0, Number(match[3]))
        );
        const severity = match[4].toLowerCase() === 'ошибка'
            ? vscode.DiagnosticSeverity.Error
            : match[4].toLowerCase() === 'предупреждение'
                ? vscode.DiagnosticSeverity.Warning
                : vscode.DiagnosticSeverity.Information;
        const diagnostic = new vscode.Diagnostic(range, match[6], severity);
        diagnostic.code = match[5];
        diagnostic.source = 'rusc';
        const values = byUri.get(uri.toString()) ?? { uri, diagnostics: [] };
        values.diagnostics.push(diagnostic);
        byUri.set(uri.toString(), values);
    }
    if (byUri.size === 0) {
        collection.delete(document.uri);
    } else {
        for (const value of byUri.values()) {
            collection.set(value.uri, value.diagnostics);
        }
    }
}

function provideSymbols(document) {
    const symbols = [];
    const classes = [];
    let depth = 0;
    for (let index = 0; index < document.lineCount; index++) {
        const text = document.lineAt(index).text;
        if (/^\s*(?:аминь|конец|совершено)\s*$/iu.test(text)) {
            depth = Math.max(0, depth - 1);
            while (classes.length > 0 && classes.at(-1).depth === depth) {
                const current = classes.pop().symbol;
                current.range = new vscode.Range(current.range.start, document.lineAt(index).range.end);
            }
            continue;
        }

        let match = /^\s*(?:(?:отвлечённый|последний)\s+)?род\s+([\p{L}_][\p{L}\p{N}_]*)/iu.exec(text);
        if (match) {
            const symbol = symbolFor(document, index, match[1], vscode.SymbolKind.Class);
            symbols.push(symbol);
            classes.push({ symbol, depth });
            depth++;
            continue;
        }
        match = /^\s*(?:всенародное|родовое|земское|сокровенное)\s+(?:(?:общинное|наследуемое|переиначенное|отвлечённое|последнее)\s+)*умение\s+\S+\s+([\p{L}_][\p{L}\p{N}_]*)/iu.exec(text);
        if (match) {
            const symbol = symbolFor(document, index, match[1], vscode.SymbolKind.Method);
            (classes.at(-1)?.symbol.children ?? symbols).push(symbol);
            if (!/\sбез\sдеяния\s*$/iu.test(text)) {
                depth++;
            }
            continue;
        }
        match = /^\s*(?:всенародный|родовой|земской|сокровенный)\s+зачин\b/iu.exec(text);
        if (match) {
            const symbol = symbolFor(document, index, 'зачин', vscode.SymbolKind.Constructor);
            (classes.at(-1)?.symbol.children ?? symbols).push(symbol);
            depth++;
            continue;
        }
        match = /^\s*(?:всенародная|родовая|земская|сокровенная)\s+(?:общинная\s+)?черта\s+\S+\s+([\p{L}_][\p{L}\p{N}_]*)/iu.exec(text);
        if (match) {
            const symbol = symbolFor(document, index, match[1], vscode.SymbolKind.Field);
            (classes.at(-1)?.symbol.children ?? symbols).push(symbol);
            continue;
        }
        if (/^\s*(?:Князь|Царь|Государь)\s*$/iu.test(text)
            || /^\s*(?:если|аще|пока|доколе|для|ступай)\b/iu.test(text)) {
            depth++;
        }
    }
    return symbols;
}

function symbolFor(document, line, name, kind) {
    const range = document.lineAt(line).range;
    return new vscode.DocumentSymbol(name, '', kind, range, range);
}

const hoverText = new Map([
    ['род', 'Объявляет объектный тип RusLang.'],
    ['черта', 'Хранимое состояние порождения рода.'],
    ['зачин', 'Конструктор порождения рода.'],
    ['умение', 'Метод рода.'],
    ['породить', 'Создать новое порождение указанного рода.'],
    ['сокровенное', 'Доступ только внутри объявившего рода.'],
    ['родовое', 'Доступ внутри рода и его наследников.'],
    ['земское', 'Доступ внутри текущей сборки.'],
    ['всенародное', 'Доступ отовсюду.'],
    ['наследуемое', 'Умение разрешено переопределять.'],
    ['переиначенное', 'Переопределение умения предка.'],
    ['сей', 'Текущее порождение рода.'],
    ['предок', 'Базовая часть наследующего рода.'],
    ['Князь', 'Точка входа программы. Синонимы: Царь, Государь.'],
    ['аминь', 'Завершает текущий блок. Синонимы: конец, совершено.']
]);

function provideHover(document, position) {
    const range = document.getWordRangeAtPosition(position, /[\p{L}_][\p{L}\p{N}_]*/u);
    if (!range) {
        return undefined;
    }
    const word = document.getText(range);
    const description = hoverText.get(word) ?? hoverText.get(word.toLowerCase());
    return description
        ? new vscode.Hover(new vscode.MarkdownString(`**${word}**\n\n${description}`), range)
        : undefined;
}

function configuration(uri) {
    return vscode.workspace.getConfiguration('ruslang', uri);
}

function workspaceDirectory(uri) {
    return (uri ? vscode.workspace.getWorkspaceFolder(uri)?.uri.fsPath : undefined)
        ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath
        ?? process.cwd();
}

function quote(value) {
    return /\s/u.test(value) ? `"${value.replaceAll('"', '\\"')}"` : value;
}

function deactivate() {}

module.exports = { activate, deactivate };
