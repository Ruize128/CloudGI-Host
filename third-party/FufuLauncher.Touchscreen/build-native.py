"""Rebuild the bundled x64 payload; --tests also builds owned-process regression fixtures."""
import argparse
import hashlib
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--toolchain', type=Path, required=True, help='LLVM-MinGW bin directory')
parser.add_argument('--tests', action='store_true', help='Also build native regression fixtures')
args = parser.parse_args()
root = Path(__file__).resolve().parent
objects = root / 'obj' / 'native-obj'
output = root / 'runtime' / 'win-x64'
objects.mkdir(parents=True, exist_ok=True)
output.mkdir(parents=True, exist_ok=True)
compiler_bin = args.toolchain.resolve()
cc = compiler_bin / 'x86_64-w64-mingw32-clang'
cxx = compiler_bin / 'x86_64-w64-mingw32-clang++'
flags = ['-fms-extensions', '-O2', '-DNDEBUG', '-DUNICODE', '-D_UNICODE',
         '-DWIN32_LEAN_AND_MEAN', '-DNOMINMAX', '-Wall', '-Wextra',
         '-Wno-missing-field-initializers']


def run(compiler, *arguments):
    subprocess.run([str(compiler), *map(str, arguments)], check=True)


minhook_objects = []
for name in ['buffer.c', 'hook.c', 'trampoline.c', 'hde/hde64.c']:
    target = objects / (Path(name).stem + '.o')
    run(cc, *flags, '-I' + str(root / 'minhook/include'), '-c', root / 'minhook/src' / name, '-o', target)
    minhook_objects.append(target)
run(cxx, *flags, '-std=c++17', '-static', '-shared', '-I' + str(root / 'minhook/include'),
    root / 'src/Touchscreen.cpp', root / 'src/Scanner/Scanner.cpp', *minhook_objects,
    '-lpsapi', '-o', output / 'CloudGI.Touchscreen.dll')
run(cxx, *flags, '-std=c++17', '-static', '-municode', root / 'src/Launcher.cpp',
    '-o', output / 'CloudGI.MobileUI.exe')

if args.tests:
    fixtures = root / 'obj' / 'fixtures'
    fixtures.mkdir(exist_ok=True)
    hook_object = objects / 'hook-fixture.o'
    run(cc, '-c', root / 'tests/HookFixture.S', '-o', hook_object)
    for name, definition, assembly in [
        ('HookFixture.exe', None, [hook_object]),
        ('HookFixtureNoCallback.exe', '-DCLOUDGI_NO_CALLBACK', [hook_object]),
        ('UnsupportedFixture.exe', '-DCLOUDGI_NO_SIGNATURES', []),
    ]:
        run(cxx, *flags, *([definition] if definition else []), '-std=c++17', '-static',
            '-municode', '-mwindows', root / 'tests/HookFixture.cpp', *assembly, '-o', fixtures / name)

(output / 'SHA256SUMS').write_text(''.join(
    hashlib.sha256((output / name).read_bytes()).hexdigest() + '  ' + name + '\n'
    for name in ['CloudGI.MobileUI.exe', 'CloudGI.Touchscreen.dll']))
print('Built x64 mobile UI payload' + (' and regression fixtures.' if args.tests else '.'))
