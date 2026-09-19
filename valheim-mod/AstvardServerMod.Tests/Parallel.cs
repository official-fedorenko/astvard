using Xunit;

// Тесты идут по одному, а не вперемешку.
//
// Almost everything under test here is a static: the sorter's chosen items, the list of
// categories, the zones. xunit runs test classes in parallel by default, so two of them
// reading and writing the same static is a test that fails once a fortnight and passes on
// the re-run - the worst kind. It cost us one such failure already, the moment the
// categories became something a test could change: LootTests read a category while
// CategoryTests was putting the built-in list back.
//
// Ставится это одной строкой и стоит ничего: весь набор идёт треть секунды.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
