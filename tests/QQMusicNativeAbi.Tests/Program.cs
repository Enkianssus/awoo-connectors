using QQMusicControlPoc;

// Pure managed interpretation of the REAL emitted x86 bytes. No inspected DLL
// is loaded, no machine code is executed, no process/UI/network API is called.
var checks = 0;
void Check(bool condition, string name)
{
    checks++;
    if (!condition) throw new InvalidOperationException(name);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidOperationException) { Check(true, name); return; }
    throw new InvalidOperationException("Expected rejection: " + name);
}
var profile = new QQMusicNativeNextProfile("22.71", Type4Contract.ClientHash, Type4Contract.CommonHash,
    0x004B0CE4, [0xE8, 0xC7, 0x91, 0x16, 0], 0xF18A, 0x2E283,
    0x4BB70, 0x4B6B0, 0x462600, 0xC6B1C8, 0x63CAF0, 0x63CC50,
    0x5134E0, 0xA0, "Exact reviewed fixture; not execution permission.");
Check(QQMusicNativeNextTransport.RequireContextStringManagerRva(profile) == 0x2B420, "exact getter");
foreach (var invalid in new[] {
    profile with { FileVersion = "22.61" }, profile with { ClientSha256 = new string('0', 64) },
    profile with { CommonSha256 = new string('0', 64) }, profile with { AddSongsRva = 0x462601 },
    profile with { SongItemSize = 0xA4 }, profile with { ExpectedPlayDispatchBytes = [0x90] } })
    Reject(() => QQMusicNativeNextTransport.BuildUiTrampoline((nint)Scenario.Data,
        (nint)Scenario.Client, (nint)Scenario.Common, invalid), "unreviewed ABI must not emit");

foreach (var count in new[] { 1, 2, 3, 7 })
foreach (var eax in new uint[] { 0, 1, 0x80004005, 0xFFFFFFFF, 0xC0000005 })
{
    var scenario = new Scenario(profile, Check) { CategoryCount = count, ArbitraryAddReturn = eax };
    scenario.Run();
    Check(scenario.NilAcquires == 1 && scenario.AddCalls == 1, "one acquire transferred to one call");
    Check(scenario.Memory.Read(Scenario.NilHeader + 12) == 2, "native nil reference balanced");
    Check(scenario.Memory.Read(Scenario.Data + 16) == 0, "arbitrary EAX is not HRESULT");
    Check(scenario.Stages.SequenceEqual(new uint[] { 1, 2, 3, 4, 5 }), "normal stage order");
    Check(scenario.Calls.SequenceEqual(new[] { "cat", "construct", "root", "helper", "count", "uin",
        "song", "string-manager", "nil", "add", "destruct", "release" }), "call order");
    Check(scenario.Memory.Read(Scenario.Data + 0xD0) == (uint)(count - 1), "hidden index is not refcount");
    Check(scenario.Memory.Read(Scenario.Data + 0xC4) == Scenario.SongId, "resolved ID retained");
    Check(scenario.Memory.Read(Scenario.Data + 0xC8) == Scenario.CategoryId, "category ID retained");
    Check(scenario.Memory.Read(Scenario.Data + 0xCC) == count, "category count retained");
}

foreach (var failure in new[] { "cat-error", "cat-null", "song-error", "manager-null", "nil-null" })
{
    var scenario = new Scenario(profile, Check) { Failure = failure };
    scenario.Run();
    Check(scenario.AddCalls == 0 && scenario.NilAcquires == 0, failure + " never calls AddSongs");
    Check(scenario.Memory.Read(Scenario.Data + 16) == 0x80004005, failure + " retains incomplete sentinel");
    Check(scenario.Memory.Read(Scenario.Data) == 5, failure + " exits via cleanup");
    Check(!scenario.Stages.Contains(4), failure + " never claims call return");
    Check(scenario.Memory.Read(Scenario.NilHeader + 12) == 2, failure + " no ownership leak");
    Check(scenario.Calls.Count(x => x == "destruct") == (failure.StartsWith("cat") ? 0 : 1), "constructed object only destroyed once");
}

// Execute the reviewed callee RELEASE instructions against the old layout.
// Counts 1/2 reach a manager pointer equal to a song ID; count 3 corrupts the
// index without crashing, explaining why repeated operations could seem safe.
foreach (var count in new[] { 1, 2, 3 })
{
    var memory = new Memory();
    memory.Map(Scenario.Data, 256);
    memory.Write(Scenario.Data + 0xC4, Scenario.SongId);
    memory.Write(Scenario.Data + 0xC8, Scenario.CategoryId);
    memory.Write(Scenario.Data + 0xCC, (uint)count);
    memory.Write(Scenario.Data + 0xD0, (uint)(count - 1));
    var invalidManager = false;
    try { Scenario.ReleaseCString(memory, Scenario.Data + 0xD4); }
    catch (InvalidMemoryException) { invalidManager = true; }
    Check(invalidManager == (count <= 2), "old layout invalid manager for count=" + count);
    Check(memory.Read(Scenario.Data + 0xD0) == unchecked((uint)(count - 2)), "old destructor corrupts index");
}

Console.WriteLine($"QQ native ABI tests passed ({checks} checks; managed x86 simulation, no QQ execution).");

sealed class Scenario(QQMusicNativeNextProfile profile, Action<bool, string> check)
{
    public const uint Client = 0x10000000, Common = 0x20000000, Code = 0x30000000, Data = Code + 0x300;
    public const uint Cat = 0x40000000, CatTable = Cat + 0x100, SongMethod = 0x41000010, ReleaseMethod = 0x41000020;
    public const uint StringManager = 0x42000000, StringTable = StringManager + 0x100, NilHeader = StringManager + 0x200;
    public const uint NilMethod = 0x41000030, SongId = 97773, CategoryId = 1234;
    public Memory Memory { get; } = new();
    public List<string> Calls { get; } = [];
    public List<uint> Stages { get; } = [];
    public int CategoryCount = 1, NilAcquires, AddCalls;
    public uint ArbitraryAddReturn = 0x80004005;
    public string Failure = "";

    public void Run()
    {
        var bytes = QQMusicNativeNextTransport.BuildUiTrampoline((nint)Data, (nint)Client, (nint)Common, profile);
        check(bytes.Length < 0x300, "code cannot overlap data");
        Memory.Map(Data, 256); Memory.Map(Cat, 0x200); Memory.Map(StringManager, 0x220);
        Memory.Write(Cat, CatTable); Memory.Write(CatTable + 0x34, SongMethod); Memory.Write(CatTable + 8, ReleaseMethod);
        Memory.Write(StringManager, StringTable); Memory.Write(StringTable + 12, NilMethod);
        Memory.Write(NilHeader, StringManager); Memory.Write(NilHeader + 12, 2);
        Memory.Write(Client + (uint)profile.HiddenCategoryIdRva, CategoryId);
        Memory.Write(Data + 0xDC, 0x13579BDF);
        Memory.OnWrite = (address, value) => { if (address == Data) Stages.Add(value); };
        var cpu = new X86(Memory, 0x70000000);
        cpu.Load(Code, bytes);
        var initial = cpu.R.ToArray();
        var initialFlags = cpu.Flags;
        void Hook(uint address, string label, Func<X86, int> body) => cpu.Hooks.Add(address, vm =>
        {
            Calls.Add(label);
            return body(vm);
        });
        void Volatile(X86 vm, uint eax)
        {
            vm.R[0] = eax; vm.R[1] = 0xBAD0C001; vm.R[2] = 0xBAD0D002;
        }
        Hook(Common + (uint)profile.GetCatManagerRva, "cat", vm =>
        {
            check(vm.Arg(0) == Data + 8, "GetCatManager output address");
            Memory.Write(vm.Arg(0), Failure == "cat-null" ? 0u : Cat);
            Volatile(vm, Failure == "cat-error" ? 0x80004005u : 0u);
            return 0; // cdecl, emitter cleans one argument
        });
        Hook(Client + (uint)profile.SongItemConstructorRva, "construct", vm =>
        {
            check(vm.R[1] == Data + 0x18, "SongItem constructor this");
            Volatile(vm, Data + 0x18); return 0;
        });
        Hook(Client + (uint)profile.GetListRootRva, "root", vm =>
        {
            check(vm.Arg(0) == 0 && vm.Arg(1) == 0, "root arguments");
            Volatile(vm, 0x43000000); return 8;
        });
        Hook(Client + (uint)profile.GetListHelperRva, "helper", vm =>
        {
            check(vm.R[1] == 0x43000000, "list helper this"); Volatile(vm, 0x43000100); return 0;
        });
        Hook(Client + (uint)profile.GetCategoryCountRva, "count", vm =>
        {
            check(vm.R[1] == 0x43000100 && vm.Arg(0) == CategoryId, "category count arguments");
            Volatile(vm, (uint)CategoryCount); return 4;
        });
        Hook(Common + (uint)profile.GetQqUinExRva, "uin", vm =>
        {
            Volatile(vm, 0x12345678); vm.R[2] = 2; return 0;
        });
        Hook(SongMethod, "song", vm =>
        {
            check(vm.Arg(0) == Cat && vm.Arg(1) == 0x12345678 && vm.Arg(2) == 2, "GetSongInfo account words");
            check(vm.Arg(3) == CategoryId && vm.Arg(4) == (uint)Math.Max(0, CategoryCount - 1), "GetSongInfo category/index");
            check(vm.Arg(5) == Data + 0x18 && vm.Arg(6) == 0, "GetSongInfo output/flags");
            Memory.Write(Data + 0x18, SongId);
            Volatile(vm, Failure == "song-error" ? 0x80004005u : 0); return 28;
        });
        Hook(Client + 0x2B420, "string-manager", vm =>
        {
            check(Memory.Read(Data) == 3, "string acquisition occurs after song resolution");
            Volatile(vm, Failure == "manager-null" ? 0u : StringManager); return 0;
        });
        Hook(NilMethod, "nil", vm =>
        {
            check(vm.R[1] == StringManager, "GetNilString this");
            if (Failure == "nil-null") { Volatile(vm, 0); return 0; }
            NilAcquires++; Memory.Write(NilHeader + 12, Memory.Read(NilHeader + 12) + 1);
            Volatile(vm, NilHeader); return 0;
        });
        Hook(Client + (uint)profile.AddSongsRva, "add", vm =>
        {
            AddCalls++;
            check(vm.R[1] == Cat && vm.R[2] == Data + 0xB8, "AddSongs ECX/EDX reset after volatile getters");
            check(vm.Arg(0) == 0 && vm.Arg(1) == NilHeader + 16, "mode=0 and owned CString character pointer");
            check(Memory.Read(vm.Arg(1) - 16) == StringManager && Memory.Read(vm.Arg(1) - 4) == 3, "native CStringData ownership");
            check(Memory.Read(vm.R[2]) == Data + 0x18 && Memory.Read(vm.R[2] + 4) == Data + 0xB8
                && Memory.Read(vm.R[2] + 8) == Data + 0xB8, "one-element SongItem vector boundaries");
            check(Memory.Read(Data) == 3 && Memory.Read(Data + 16) == 0x80004005, "no premature completed sentinel");
            ReleaseCString(Memory, vm.Arg(1));
            Volatile(vm, ArbitraryAddReturn); return 0; // callee consumes CString, caller cleans 8 bytes
        });
        Hook(Client + (uint)profile.SongItemDestructorRva, "destruct", vm =>
        {
            check(vm.R[1] == Data + 0x18, "SongItem destructor this"); Volatile(vm, 0xCCCCCCCC); return 0;
        });
        Hook(ReleaseMethod, "release", vm =>
        {
            check(vm.Arg(0) == Cat, "COM Release this"); Volatile(vm, 1); return 4;
        });
        cpu.Run(Code);
        check(cpu.R.SequenceEqual(initial), "all registers and caller stack restored");
        check(cpu.Flags == initialFlags, "flags restored");
        check(Memory.Read(Data + 0xDC) == 0x13579BDF, "diagnostic canary untouched");
    }

    public static void ReleaseCString(Memory memory, uint characters)
    {
        // Exact QQMusic.dll SHA256 0108E68B...157624BE, RVA 0x4630BE..0x4630D3;
        // appended RET replaces the surrounding epilogue only. This fixture
        // tests its actual pointer/refcount instructions, not a C# imitation.
        byte[] release = Convert.FromHexString("8B550C83C2F0F00FC15A0C4B85DB7F088B0A528B01FF5004C3");
        var cpu = new X86(memory, 0x71000000);
        cpu.Load(0x51000000, release);
        cpu.R[5] = 0x71000600;
        memory.Write(cpu.R[5] + 12, characters);
        cpu.R[3] = 0xFFFFFFFF;
        cpu.Run(0x51000000);
    }
}

sealed class InvalidMemoryException(uint address) : Exception($"Unmapped simulated memory: 0x{address:X8}");

sealed class Memory
{
    readonly Dictionary<uint, byte> bytes = [];
    public Action<uint, uint>? OnWrite;
    public void Map(uint start, int length) { for (var i = 0; i < length; i++) bytes[start + (uint)i] = 0; }
    public byte Byte(uint address) => bytes.TryGetValue(address, out var b) ? b : throw new InvalidMemoryException(address);
    public uint Read(uint address) => (uint)(Byte(address) | Byte(address + 1) << 8 | Byte(address + 2) << 16 | Byte(address + 3) << 24);
    public void Write(uint address, uint value)
    {
        for (var i = 0; i < 4; i++) bytes[address + (uint)i] = (byte)(value >> (8 * i));
        OnWrite?.Invoke(address, value);
    }
    public void Copy(uint address, byte[] values) { for (var i = 0; i < values.Length; i++) bytes[address + (uint)i] = values[i]; }
}

// Only the emitted integer x86 subset is supported. Unknown opcodes fail,
// rather than skipping bytes and accidentally passing a changed trampoline.
sealed class X86
{
    const uint Stop = 0xDEADBEEF;
    readonly Memory memory;
    public uint[] R { get; } = [0x11111111, 0x22222222, 0x33333333, 0x44444444, 0, 0x55555555, 0x66666666, 0x77777777];
    public uint Flags = 0x202;
    uint pc;
    public Dictionary<uint, Func<X86, int>> Hooks { get; } = [];
    public X86(Memory memory, uint stack)
    {
        this.memory = memory; memory.Map(stack, 0x1000); R[4] = stack + 0x800;
    }
    public void Load(uint address, byte[] bytes) => memory.Copy(address, bytes);
    byte Fetch() => memory.Byte(pc++);
    uint U32() { var v = memory.Read(pc); pc += 4; return v; }
    void Push(uint value) { R[4] -= 4; memory.Write(R[4], value); }
    uint Pop() { var v = memory.Read(R[4]); R[4] += 4; return v; }
    public uint Arg(int index) => memory.Read(R[4] + 4u + (uint)index * 4);
    readonly record struct Operand(bool IsRegister, uint Value);
    (int Reg, Operand Rm) Decode()
    {
        var b = Fetch(); var mod = b >> 6; var reg = (b >> 3) & 7; var rm = b & 7;
        if (mod == 3) return (reg, new(true, (uint)rm));
        if (rm == 4) throw new InvalidOperationException("SIB is outside this fixture subset");
        uint address = mod == 0 && rm == 5 ? U32() : R[rm];
        if (mod == 1) address = unchecked(address + (uint)(sbyte)Fetch());
        else if (mod == 2) address = unchecked(address + U32());
        return (reg, new(false, address));
    }
    uint Read(Operand o) => o.IsRegister ? R[o.Value] : memory.Read(o.Value);
    void Write(Operand o, uint value) { if (o.IsRegister) R[o.Value] = value; else memory.Write(o.Value, value); }
    void SetFlags(uint result, bool overflow = false)
    {
        Flags = (Flags & ~0x8C0u) | (result == 0 ? 0x40u : 0) | (result & 0x80000000u) >> 24 | (overflow ? 0x800u : 0);
    }
    uint Add(uint a, uint b) { var c = unchecked(a + b); SetFlags(c, (~(a ^ b) & (a ^ c) & 0x80000000u) != 0); return c; }
    uint Sub(uint a, uint b) { var c = unchecked(a - b); SetFlags(c, ((a ^ b) & (a ^ c) & 0x80000000u) != 0); return c; }
    bool Zero => (Flags & 0x40) != 0;
    bool Sign => (Flags & 0x80) != 0;
    bool Overflow => (Flags & 0x800) != 0;
    void Call(uint target)
    {
        Push(pc);
        if (Hooks.TryGetValue(target, out var hook)) { var clean = hook(this); pc = Pop(); R[4] += (uint)clean; }
        else pc = target;
    }
    public void Run(uint address)
    {
        pc = address; Push(Stop);
        for (var steps = 0; pc != Stop; steps++)
        {
            if (steps > 5000) throw new InvalidOperationException("Interpreter step bound exceeded");
            var at = pc; var op = Fetch();
            if (op == 0xF0) op = Fetch(); // single-threaded simulation of LOCK atomic operation
            if (op >= 0xB8 && op <= 0xBF) { R[op - 0xB8] = U32(); continue; }
            if (op >= 0x50 && op <= 0x57) { Push(R[op - 0x50]); continue; }
            if (op >= 0x48 && op <= 0x4F) { var reg = op - 0x48; R[reg] = Sub(R[reg], 1); continue; }
            switch (op)
            {
                case 0x9C: Push(Flags); break;
                case 0x9D: Flags = Pop(); break;
                case 0x60:
                    var esp = R[4]; foreach (var reg in new[] { 0, 1, 2, 3 }) Push(R[reg]);
                    Push(esp); foreach (var reg in new[] { 5, 6, 7 }) Push(R[reg]); break;
                case 0x61:
                    foreach (var reg in new[] { 7, 6, 5 }) R[reg] = Pop(); Pop();
                    foreach (var reg in new[] { 3, 2, 1, 0 }) R[reg] = Pop(); break;
                case 0x6A: Push(unchecked((uint)(sbyte)Fetch())); break;
                case 0x68: Push(U32()); break;
                case 0xA1: R[0] = memory.Read(U32()); break;
                case 0x05: R[0] = Add(R[0], U32()); break;
                case 0x33:
                    { var (reg, rm) = Decode(); R[reg] ^= Read(rm); SetFlags(R[reg]); break; }
                case 0x8D:
                    { var (reg, rm) = Decode(); if (rm.IsRegister) throw new InvalidOperationException("LEA register"); R[reg] = rm.Value; break; }
                case 0x8B:
                    { var (reg, rm) = Decode(); R[reg] = Read(rm); break; }
                case 0x89:
                    { var (reg, rm) = Decode(); Write(rm, R[reg]); break; }
                case 0x85:
                    { var (reg, rm) = Decode(); SetFlags(R[reg] & Read(rm)); break; }
                case 0xC7:
                    { var (group, rm) = Decode(); if (group != 0) throw new InvalidOperationException("C7 group"); Write(rm, U32()); break; }
                case 0x83:
                    { var (group, rm) = Decode(); var imm = unchecked((uint)(sbyte)Fetch());
                      if (group == 0) Write(rm, Add(Read(rm), imm));
                      else if (group == 7) Sub(Read(rm), imm);
                      else throw new InvalidOperationException("83 group " + group); break; }
                case 0xFF:
                    { var (group, rm) = Decode(); var value = Read(rm);
                      if (group == 2) Call(value); else if (group == 6) Push(value);
                      else throw new InvalidOperationException("FF group " + group); break; }
                case 0x0F:
                    { var next = Fetch();
                      if (next == 0xC1) { var (reg, rm) = Decode(); var old = Read(rm); Write(rm, Add(old, R[reg])); R[reg] = old; break; }
                      var offset = U32();
                      var jump = next switch { 0x84 => Zero, 0x88 => Sign, 0x89 => !Sign, _ => throw new InvalidOperationException("0F branch " + next) };
                      if (jump) pc = unchecked(pc + offset); break; }
                case 0x7F:
                    { var offset = (sbyte)Fetch(); if (!Zero && Sign == Overflow) pc = unchecked(pc + (uint)offset); break; }
                case 0xC3: pc = Pop(); break;
                default: throw new InvalidOperationException($"Unsupported opcode {op:X2} at {at:X8}");
            }
        }
    }
}
