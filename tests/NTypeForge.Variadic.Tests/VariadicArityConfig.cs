// Drives the analyzer-wired generation for this test assembly: the runtime tests use arities up to
// 12 (Zip of 3/5 sequences, VariadicFunc/VariadicAction of 3 args, ForEach of 2). The attribute
// type itself is supplied by the generator's post-initialization output.
[assembly: NTypeForge.Variadic.VariadicArity(12)]
