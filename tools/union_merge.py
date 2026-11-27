"""Resolve registration-file merge conflicts by keeping both sides (module lists, project refs)."""
import re, sys
def merge_array(a, b):
    m1, m2 = re.search(r'\[(.*)\]', a), re.search(r'\[(.*)\]', b)
    items = [x.strip() for x in m1.group(1).split(',')]
    for x in [x.strip() for x in m2.group(1).split(',')]:
        if x not in items: items.append(x)
    return a[:m1.start(1)] + ', '.join(items) + a[m1.end(1):]
for path in sys.argv[1:]:
    text = open(path).read()
    pattern = re.compile(r'<<<<<<< [^\n]*\n(.*?)=======\n(.*?)>>>>>>> [^\n]*\n', re.S)
    def resolve(m):
        ours, theirs = m.group(1), m.group(2)
        ol, tl = ours.splitlines(), theirs.splitlines()
        if len(ol) == 1 and len(tl) == 1 and '[' in ol[0] and ']' in ol[0]:
            return merge_array(ol[0], tl[0]) + '\n'
        if ol and tl and ol[-1].rstrip().endswith('));') and tl[-1].rstrip().endswith('));'):
            new = [l for l in tl if l not in ol]
            if not new:
                return ours
            head = ol[:-1] + [ol[-1].rstrip()[:-2] + ',']
            return ''.join(l + '\n' for l in head + new)
        return ours + ''.join(l + '\n' for l in tl if l not in ol)
    text = pattern.sub(resolve, text)
    open(path, 'w').write(text)
    print(path, 'conflicts left:', text.count('<<<<<<<'))
