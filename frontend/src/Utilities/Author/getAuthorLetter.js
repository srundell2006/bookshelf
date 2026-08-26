//
// Work out which letter bucket an author belongs to.
//
// Root folders can each claim a set of surname initials, and adding an author
// should default to the folder that claims theirs. The library files authors by
// surname, so the letter has to come from the surname - not the first name.
//
// sortNameLastFirst is already surname-first ("le Carre, John", "King, Stephen"),
// which also gets multi-word surnames right: "John le Carre" belongs under L, not
// C. Only if no surname-first form is available do we fall back to a natural-order
// name, where the surname is taken to be the last whitespace-separated word.
//
// Accents are folded so "Mo Xiang Tong Xiu" and "Mò Xiāng ..." land in the same
// place rather than one of them having no bucket at all.
//
// Returns a single uppercase A-Z character, or null when the name yields nothing
// usable (non-Latin scripts, names starting with a digit or symbol).

const COMBINING_MARKS = /[̀-ͯ]/g;

function fold(value) {
  return value.normalize('NFD').replace(COMBINING_MARKS, '').trim();
}

function initialOf(value) {
  const letter = value.charAt(0).toUpperCase();

  return (/^[A-Z]$/).test(letter) ? letter : null;
}

export default function getAuthorLetter(author) {
  if (!author) {
    return null;
  }

  // Surname-first forms: the first character is already the one we want.
  const lastFirst = author.sortNameLastFirst || author.authorNameLastFirst;

  if (lastFirst && typeof lastFirst === 'string') {
    return initialOf(fold(lastFirst));
  }

  // Natural order ("Matt Dinniman") - the surname is the last word, so taking the
  // first character here would file the author under their given name instead.
  const natural = author.sortName || author.authorName;

  if (!natural || typeof natural !== 'string') {
    return null;
  }

  const words = fold(natural).split(/\s+/).filter((w) => w.length > 0);

  if (!words.length) {
    return null;
  }

  return initialOf(words[words.length - 1]);
}
