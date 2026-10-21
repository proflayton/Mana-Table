const fs = require('node:fs');
const path = require('node:path');

const blank = text => text.replace(/[^\n]/g, ' ');
function withoutExamples(text, inline = true) {
  let fence;
  const lines = text.replace(/\r/g, '').split('\n').map(line => {
    const marker = line.match(/^ {0,3}(`{3,}|~{3,})(.*)$/);
    if (fence) {
      if (marker && marker[1][0] === fence[0] && marker[1].length >= fence.length && !marker[2].trim()) fence = null;
      return blank(line);
    }
    if (marker) { fence = marker[1]; return blank(line); }
    return line;
  }).join('\n').replace(/<!--[\s\S]*?-->/g, blank);
  return inline ? lines.replace(/(`+)([\s\S]*?)\1(?!`)/g, blank) : lines;
}
const referenceKey = value => value.trim().replace(/\s+/g, ' ').toLowerCase();
function links(text) {
  let source = withoutExamples(text);
  const refs = new Map(), found = [];
  const add = (target, at) => found.push({ target, line: source.slice(0, at).split('\n').length });
  const destination = value => value.startsWith('<') ? value.slice(1, -1) : value;
  source = source.replace(/^ {0,3}\[([^\]]+)\]:\s*(<[^>]+>|\S+).*$/gm, (whole, id, target) => {
    refs.set(referenceKey(id), destination(target)); return blank(whole);
  });
  source = source.replace(/!?\[([^\]\n]*)\]\(\s*(<[^>\n]+>|[^\s)]+)(?:\s+[^)\n]*)?\)/g, (whole, label, target, at) => {
    if (!whole.startsWith('!')) add(destination(target), at);
    return blank(whole);
  });
  source = source.replace(/!?\[([^\]\n]+)\]\[([^\]\n]*)\]/g, (whole, label, id, at) => {
    if (!whole.startsWith('!') && refs.has(referenceKey(id || label))) add(refs.get(referenceKey(id || label)), at);
    return blank(whole);
  });
  source.replace(/!?\[([^\]\n]+)\]/g, (whole, id, at) => {
    if (!whole.startsWith('!') && refs.has(referenceKey(id))) add(refs.get(referenceKey(id)), at);
    return whole;
  });
  return found;
}
function headingSlugs(text) {
  const source = withoutExamples(text, false), lines = source.split('\n'), seen = new Set(), counts = new Map();
  const entity = value => value.replace(/&(?:amp|lt|gt|quot|apos|nbsp);|&#(?:x[\da-f]+|\d+);/gi, item => {
    const named = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&apos;': "'", '&nbsp;': ' ' };
    const code = parseInt(item.slice(item[2]?.toLowerCase() === 'x' ? 3 : 2, -1), item[2]?.toLowerCase() === 'x' ? 16 : 10);
    return named[item.toLowerCase()] ?? (code >= 0 && code <= 0x10ffff ? String.fromCodePoint(code) : item);
  });
  for (let i = 0; i < lines.length; i++) {
    const atx = lines[i].match(/^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$/);
    let title = atx?.[1];
    if (!title && lines[i].trim() && /^ {0,3}(?:=+|-+)\s*$/.test(lines[i + 1] ?? '')) title = lines[i++].trim();
    if (!title) continue;
    title = entity(title.replace(/!?\[([^\]]+)\]\([^)]*\)/g, '$1').replace(/<[^>]*>/g, '')
      .replace(/(^|\s)_([^_]+)_(?=\s|$)/g, '$1$2').replace(/[`*~]/g, ''));
    const base = title.toLowerCase().replace(/[^\p{L}\p{N}\p{M}_\-\s]/gu, '').trim().replace(/\s/g, '-');
    let slug = base, count = counts.get(base) ?? 0;
    while (seen.has(slug)) slug = base + '-' + ++count;
    counts.set(base, count); seen.add(slug);
  }
  for (const anchor of source.matchAll(/<a\s+[^>]*(?:id|name)=["']([^"']+)["'][^>]*>/gi)) seen.add(anchor[1]);
  return seen;
}

// Pure with an in-memory lookup: { [repoRelativePath]: text } and lookup(path)
// returning { text?, directory? }, or undefined for a missing repository entry.
function checkDocuments(documents, lookup) {
  const errors = [];
  for (const [file, text] of Object.entries(documents)) for (const link of links(text)) {
    if (/^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(link.target)) continue;
    try {
      const [rawPath, ...fragments] = link.target.split('#');
      const local = decodeURIComponent(rawPath.split('?')[0]);
      const fragment = decodeURIComponent(fragments.join('#'));
      const target = local ? path.posix.normalize(local.startsWith('/') ? local.slice(1) : path.posix.join(path.posix.dirname(file), local)) : file;
      if (target === '..' || target.startsWith('../')) throw new Error('link leaves the repository');
      let entry = lookup(target), headingFile = target;
      if (!entry) throw new Error(`missing local target: ${link.target}`);
      if (fragment && entry.directory) { headingFile = path.posix.join(target, 'README.md'); entry = lookup(headingFile); }
      if (fragment && /\.(?:md|markdown)$/i.test(headingFile) && (!entry || !headingSlugs(entry.text ?? '').has(fragment)))
        throw new Error(`missing heading #${fragment} in ${headingFile}`);
    } catch (error) { errors.push({ file, line: link.line, message: error.message }); }
  }
  return errors;
}
function maintainedPaths(root) {
  const files = ['README.md', 'CONTRIBUTING.md', 'mana-native/README.md', 'mana-native/START-HERE.txt',
    'forge-api/README.md', 'forge-desktop/README.md', 'forge-desktop/BETA.md'];
  function walk(directory) {
    if (!fs.existsSync(path.join(root, directory))) return;
    for (const entry of fs.readdirSync(path.join(root, directory), { withFileTypes: true })) {
      const name = directory + '/' + entry.name;
      if (entry.isDirectory()) walk(name);
      else if (/(?:^|\/)(?:pull_request_template(?:\.md)?|issue_template)(?:\/|$)/i.test(name) && /\.(?:md|ya?ml)$/i.test(name)) files.push(name);
    }
  }
  walk('.github');
  for (const entry of fs.readdirSync(path.join(root, 'docs/Development')))
    if (/^Mana-Table-.*\.md$/.test(entry)) files.push('docs/Development/' + entry);
  return [...new Set(files)].sort();
}
function checkRepository(root) {
  const documents = {}, errors = [];
  for (const file of maintainedPaths(root)) {
    if (!fs.existsSync(path.join(root, file))) errors.push({ file, line: 1, message: 'missing maintained document' });
    else documents[file] = fs.readFileSync(path.join(root, file), 'utf8');
  }
  const cache = new Map();
  const lookup = file => {
    if (!cache.has(file)) {
      const target = path.join(root, file);
      cache.set(file, !fs.existsSync(target) ? undefined : fs.statSync(target).isDirectory() ? { directory: true }
        : { text: /\.(?:md|markdown)$/i.test(file) ? fs.readFileSync(target, 'utf8') : '' });
    }
    return cache.get(file);
  };
  return { files: Object.keys(documents).length, errors: errors.concat(checkDocuments(documents, lookup)) };
}
if (require.main === module) {
  const result = checkRepository(path.resolve(__dirname, '../..'));
  for (const error of result.errors) console.error(`${error.file}:${error.line}: ${error.message}`);
  if (result.errors.length) process.exitCode = 1;
  else console.log(`Checked local documentation links and Markdown headings in ${result.files} maintained files.`);
}
module.exports = { links, headingSlugs, checkDocuments, maintainedPaths, checkRepository };
