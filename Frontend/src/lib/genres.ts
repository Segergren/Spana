import { type CatalogItem, type Category } from './api';

export interface Genre {
  id: string;
  name: string;
  color: string;
  words: string[];
}

export const GENRES: Genre[] = [
  {
    id: 'sport',
    name: 'Sport',
    color: '#1d7a46',
    words: [
      'sport',
      'fotboll',
      'football',
      'hockey',
      'golf',
      'espn',
      'eurosport',
      'arena',
      'dazn',
      'ufc',
      'fight',
      'nfl',
      'nba',
      'nhl',
      'mlb',
      'mls',
      'wwe',
      'motor',
      'racing',
      'tennis',
      'padel',
      'cricket',
      'rugby',
      'nascar',
      'event',
      'ppv',
      'viaplay',
      'expressen',
      'deporte',
      'esporte',
      'futbol',
      'fussball',
      'fußball',
      'calcio',
      'equipe',
      'tsn',
      'bein',
      'supersport',
      'setanta',
      'tudn',
      'tyc',
      'спорт',
      'матч',
    ],
  },
  {
    id: 'news',
    name: 'News',
    color: '#1f5c9e',
    words: [
      'news',
      'nyheter',
      'nyheder',
      'nieuws',
      'nachrichten',
      'noticia',
      'notizie',
      'uutiset',
      'actualit',
      'informa',
      'haber',
      'tagesschau',
      'welt',
      'franceinfo',
      'tg24',
      'tgcom',
      'cnn',
      'sky news',
      'al jazeera',
      'euronews',
      'bloomberg',
      'cnbc',
      'msnbc',
      'cgtn',
      'france 24',
      'svt24',
      'tv2 news',
      'tv4 nyheter',
      'новости',
      'вести',
    ],
  },
  {
    id: 'kids',
    name: 'Kids',
    color: '#c46a1c',
    words: [
      'kids',
      'barn',
      'junior',
      'disney',
      'nick',
      'cartoon',
      'toon',
      'boomerang',
      'baby',
      'cartoonito',
      'svtb',
      'duck',
      'jim jam',
      'kika',
      'kinder',
      'infantil',
      'enfant',
      'ninos',
      'bambini',
      'clan',
      'gulli',
      'boing',
      'ramasjang',
      'cbeebies',
      'cbbc',
      'детск',
      'мульт',
    ],
  },
  {
    id: 'movies',
    name: 'Movies',
    color: '#a3283c',
    words: [
      'cinema',
      'film',
      'movie',
      'movies',
      'hbo',
      'tcm',
      'amc',
      'paramount',
      'showtime',
      'starz',
      'cine',
      'kino',
      'pelicula',
      'кино',
      'фильм',
    ],
  },
  {
    id: 'docs',
    name: 'Documentary',
    color: '#24726a',
    words: [
      'discovery',
      'national geographic',
      'nat geo',
      'history',
      'historia',
      'animal planet',
      'docu',
      'doku',
      'dokument',
      'science',
      'investigation',
      'explore',
      'nature',
      'curiosity',
    ],
  },
  {
    id: 'music',
    name: 'Music',
    color: '#6b3aa0',
    words: [
      'mtv',
      'music',
      'musik',
      'musica',
      'musique',
      'muzyka',
      'muzika',
      'vh1',
      'clubland',
      'kerrang',
      'trace',
      'stingray',
      'vevo',
      'mezzo',
      'xite',
      'музык',
    ],
  },
];

export const GROUP_COLORS = ['#2d4a6b', '#4a3a6b', '#6b3a4a', '#3a6b5a', '#6b5a2d', '#2d6b6b', '#5a2d6b', '#6b442d', '#2d6b3e', '#3e4f63'];

export function colorFor(text: string) {
  let h = 0;
  for (const ch of text) h = (h * 31 + ch.charCodeAt(0)) | 0;
  return GROUP_COLORS[Math.abs(h) % GROUP_COLORS.length];
}

// Like normalize, but letters of every script stay so Cyrillic genre words can match.
function fold(s: string) {
  return s
    .toLowerCase()
    .normalize('NFD')
    .replace(/\p{M}/gu, '')
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim();
}

export function genreMatcher(categories: Category[]) {
  const groupNames = new Map(categories.map((c) => [c.id, fold(c.name)]));
  const cache = new Map<string, string>();
  return (genre: Genre, item: CatalogItem) => {
    let text = cache.get(item.id);
    if (text === undefined) {
      text = ` ${fold(item.name)} ${groupNames.get(item.categoryId) ?? ''} `;
      cache.set(item.id, text);
    }
    return genre.words.some((w) => text!.includes(` ${w}`));
  };
}
