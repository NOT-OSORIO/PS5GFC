// PS5GFC — PS5 Game Format Converter
// Copyright (C) 2026 OSØRIO
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

use std::sync::atomic::{AtomicU8, Ordering};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Lang {
    Pt = 0,
    En = 1,
    Es = 2,
    Fr = 3,
    Ru = 4,
}

static LANG: AtomicU8 = AtomicU8::new(Lang::En as u8);

impl Lang {
    pub fn parse(code: &str) -> Lang {
        let c = code.trim().to_ascii_lowercase();
        match c.split(['-', '_']).next().unwrap_or("") {
            "pt" => Lang::Pt,
            "es" => Lang::Es,
            "fr" => Lang::Fr,
            "ru" => Lang::Ru,
            _ => Lang::En,
        }
    }
    fn from_u8(v: u8) -> Lang {
        match v {
            0 => Lang::Pt,
            2 => Lang::Es,
            3 => Lang::Fr,
            4 => Lang::Ru,
            _ => Lang::En,
        }
    }
}

pub fn set_lang(code: &str) {
    LANG.store(Lang::parse(code) as u8, Ordering::Relaxed);
}

pub fn lang() -> Lang {
    Lang::from_u8(LANG.load(Ordering::Relaxed))
}

pub fn tr(key: &'static str) -> &'static str {
    match TABLE.iter().find(|(k, _)| *k == key) {
        Some((_, v)) => {
            let s = v[lang() as usize];
            if s.is_empty() {
                v[Lang::En as usize]
            } else {
                s
            }
        }
        None => key,
    }
}

pub fn tr_en(key: &'static str) -> &'static str {
    match TABLE.iter().find(|(k, _)| *k == key) {
        Some((_, v)) if !v[Lang::En as usize].is_empty() => v[Lang::En as usize],
        _ => key,
    }
}

#[macro_export]
macro_rules! tl {
    ($key:literal $(, $name:ident = $val:expr)* $(,)?) => {{
        #[allow(unused_mut)]
        let mut s = String::from($crate::i18n::tr_en($key));
        $( s = s.replace(concat!("{", stringify!($name), "}"), &($val).to_string()); )*
        s
    }};
}

#[macro_export]
macro_rules! t {
    ($key:literal $(, $name:ident = $val:expr)* $(,)?) => {{
        #[allow(unused_mut)]
        let mut s = String::from($crate::i18n::tr($key));
        $( s = s.replace(concat!("{", stringify!($name), "}"), &($val).to_string()); )*
        s
    }};
}

static TABLE: &[(&str, [&str; 5])] = &[

    ("log.opening", [
        "Abrindo origem: {path}",
        "Opening source: {path}",
        "Abriendo origen: {path}",
        "Ouverture de la source : {path}",
        "Открытие источника: {path}",
    ]),
    ("log.summary", [
        "{src} → {dst} ({files} arquivos, {size})",
        "{src} → {dst} ({files} files, {size})",
        "{src} → {dst} ({files} archivos, {size})",
        "{src} → {dst} ({files} fichiers, {size})",
        "{src} → {dst} (файлов: {files}, {size})",
    ]),
    ("log.ampr", [
        "Gerando ampr_emu.index (AMPR) para o destino; a origem não é alterada",
        "Generating ampr_emu.index (AMPR) for the output; the source is not modified",
        "Generando ampr_emu.index (AMPR) para el destino; el origen no se modifica",
        "Génération de ampr_emu.index (AMPR) pour la sortie ; la source n'est pas modifiée",
        "Создание ampr_emu.index (AMPR) для результата; источник не изменяется",
    ]),
    ("log.extracting", [
        "Extraindo arquivos…",
        "Extracting files…",
        "Extrayendo archivos…",
        "Extraction des fichiers…",
        "Извлечение файлов…",
    ]),
    ("log.reuse", [
        "Reaproveitando a imagem interna (sem reanalisar os arquivos)",
        "Reusing the inner image (files are not re-read)",
        "Reutilizando la imagen interna (sin releer los archivos)",
        "Réutilisation de l'image interne (sans relire les fichiers)",
        "Повторное использование внутреннего образа (файлы не перечитываются)",
    ]),
    ("log.planning", [
        "Planejando o layout {fs}…",
        "Planning the {fs} layout…",
        "Planificando el diseño {fs}…",
        "Planification de la disposition {fs}…",
        "Планирование структуры {fs}…",
    ]),
    ("log.logical", [
        "Imagem lógica: {size}",
        "Logical image: {size}",
        "Imagen lógica: {size}",
        "Image logique : {size}",
        "Логический образ: {size}",
    ]),
    ("log.compress", [
        "Comprimindo com {threads} threads (zlib, nível {level})…",
        "Compressing with {threads} threads (zlib, level {level})…",
        "Comprimiendo con {threads} hilos (zlib, nivel {level})…",
        "Compression avec {threads} threads (zlib, niveau {level})…",
        "Сжатие в {threads} потоков (zlib, уровень {level})…",
    ]),
    ("log.pfsc_stats", [
        "PFSC: {blocks} blocos (zero {zero}, crus {raw}, incomprimíveis {skipped}) | tempo somado: leitura {read} s, compressão {enc} s, gravação {write} s",
        "PFSC: {blocks} blocks (zero {zero}, raw {raw}, incompressible {skipped}) | summed time: read {read} s, compress {enc} s, write {write} s",
        "PFSC: {blocks} bloques (cero {zero}, crudos {raw}, incompresibles {skipped}) | tiempo sumado: lectura {read} s, compresión {enc} s, escritura {write} s",
        "PFSC : {blocks} blocs (zéro {zero}, bruts {raw}, incompressibles {skipped}) | temps cumulé : lecture {read} s, compression {enc} s, écriture {write} s",
        "PFSC: блоков {blocks} (нулевых {zero}, несжатых {raw}, несжимаемых {skipped}) | суммарное время: чтение {read} с, сжатие {enc} с, запись {write} с",
    ]),
    ("log.pkg.prepare", [
        "Preparando build do PKG…",
        "Preparing PKG build…",
        "Preparando compilación del PKG…",
        "Préparation du build PKG…",
        "Подготовка сборки PKG…",
    ]),
    ("log.pkg.stage", [
        "Extraindo a origem para uma pasta temporária antes de empacotar…",
        "Extracting the source to a temporary folder before packaging…",
        "Extrayendo el origen a una carpeta temporal antes de empaquetar…",
        "Extraction de la source vers un dossier temporaire avant empaquetage…",
        "Извлечение источника во временную папку перед упаковкой…",
    ]),
    ("log.pkg.build", [
        "Gerando pacote debug PKG (FPKG)…",
        "Building debug PKG package (FPKG)…",
        "Generando paquete debug PKG (FPKG)…",
        "Génération du paquet debug PKG (FPKG)…",
        "Сборка debug-пакета PKG (FPKG)…",
    ]),
    ("log.pkg.direct", [
        "O builder lê a imagem direto, sem extrair o jogo.",
        "The builder reads the image directly, without extracting the game.",
        "El builder lee la imagen directamente, sin extraer el juego.",
        "Le builder lit l'image directement, sans extraire le jeu.",
        "Сборщик читает образ напрямую, без извлечения игры.",
    ]),
    ("log.pkg.verified", [
        "PKG verificado pelo motor do PS5 PKG Tool (estrutura, digests e sistema de arquivos interno).",
        "PKG verified by the PS5 PKG Tool engine (structure, digests and inner filesystem).",
        "PKG verificado por el motor de PS5 PKG Tool (estructura, digests y sistema de archivos interno).",
        "PKG vérifié par le moteur de PS5 PKG Tool (structure, digests et système de fichiers interne).",
        "PKG проверен движком PS5 PKG Tool (структура, дайджесты и внутренняя файловая система).",
    ]),
    ("log.pkgsrc.list", [
        "Lendo o pacote PKG (lista de arquivos e sce_sys)…",
        "Reading the PKG package (file list and sce_sys)…",
        "Leyendo el paquete PKG (lista de archivos y sce_sys)…",
        "Lecture du paquet PKG (liste des fichiers et sce_sys)…",
        "Чтение пакета PKG (список файлов и sce_sys)…",
    ]),
    ("log.pkgsrc.extract", [
        "Extraindo o PKG para uma pasta de trabalho antes de converter…",
        "Extracting the PKG to a work folder before converting…",
        "Extrayendo el PKG a una carpeta de trabajo antes de convertir…",
        "Extraction du PKG vers un dossier de travail avant la conversion…",
        "Извлечение PKG в рабочую папку перед конвертацией…",
    ]),
    ("err.pkgsrc_not_staged", [
        "O arquivo {path} do PKG ainda não foi extraído (o PKG só pode ser lido inteiro na hora de converter).",
        "The PKG file {path} has not been extracted yet (a PKG can only be read in full when converting).",
        "El archivo {path} del PKG aún no se extrajo (un PKG solo se puede leer completo al convertir).",
        "Le fichier {path} du PKG n'a pas encore été extrait (un PKG ne se lit en entier qu'au moment de la conversion).",
        "Файл {path} из PKG ещё не извлечён (PKG читается целиком только при конвертации).",
    ]),
    ("err.pkgsrc_empty", [
        "O PKG não contém arquivos legíveis (só pacotes debug/FPKG podem ser abertos).",
        "The PKG has no readable files (only debug/FPKG packages can be opened).",
        "El PKG no contiene archivos legibles (solo se pueden abrir paquetes debug/FPKG).",
        "Le PKG ne contient aucun fichier lisible (seuls les paquets debug/FPKG peuvent être ouverts).",
        "В PKG нет читаемых файлов (открываются только debug/FPKG-пакеты).",
    ]),
    ("err.pkgsrc_space", [
        "Pouco espaço em {path} para extrair o PKG antes de converter: livre {free}, necessário {need}. Escolha uma saída em um disco com mais espaço.",
        "Not enough space in {path} to extract the PKG before converting: free {free}, needed {need}. Choose an output on a drive with more space.",
        "Poco espacio en {path} para extraer el PKG antes de convertir: libre {free}, necesario {need}. Elige una salida en un disco con más espacio.",
        "Pas assez d'espace dans {path} pour extraire le PKG avant la conversion : libre {free}, nécessaire {need}. Choisissez une sortie sur un disque avec plus d'espace.",
        "Недостаточно места в {path} для извлечения PKG перед конвертацией: свободно {free}, нужно {need}. Выберите вывод на диск с большим объёмом.",
    ]),
    ("log.verify", [
        "Verificando o resultado (conteúdo de cada arquivo)…",
        "Verifying the result (content of every file)…",
        "Verificando el resultado (contenido de cada archivo)…",
        "Vérification du résultat (contenu de chaque fichier)…",
        "Проверка результата (содержимое каждого файла)…",
    ]),
    ("log.done", [
        "Concluído: {path}",
        "Done: {path}",
        "Completado: {path}",
        "Terminé : {path}",
        "Готово: {path}",
    ]),

    ("err.same_format", [
        "A origem já está neste formato; escolha outro destino.",
        "The source is already in this format; choose a different destination.",
        "El origen ya está en este formato; elige otro destino.",
        "La source est déjà dans ce format ; choisissez une autre destination.",
        "Источник уже в этом формате; выберите другой формат.",
    ]),
    ("err.same_path", [
        "A saída não pode ser o mesmo caminho da origem.",
        "The output cannot be the same path as the source.",
        "La salida no puede ser la misma ruta que el origen.",
        "La sortie ne peut pas être le même chemin que la source.",
        "Выходной путь не может совпадать с источником.",
    ]),
    ("err.no_contentid", [
        "sce_sys/param.json ausente ou sem contentId; não é possível gerar PKG.",
        "sce_sys/param.json is missing or has no contentId; PKG cannot be built.",
        "sce_sys/param.json no existe o no tiene contentId; no se puede generar PKG.",
        "sce_sys/param.json est absent ou sans contentId ; impossible de générer un PKG.",
        "sce_sys/param.json отсутствует или не содержит contentId; невозможно собрать PKG.",
    ]),
    ("err.pkg_console", [
        "PKG só pode ser gerado no PC; escolha uma pasta local de destino.",
        "PKG can only be built on the PC; choose a local destination folder.",
        "PKG solo puede generarse en el PC; elige una carpeta local de destino.",
        "Le PKG ne peut être généré que sur le PC ; choisissez un dossier local.",
        "PKG можно собрать только на ПК; выберите локальную папку назначения.",
    ]),
    ("err.pkg_stage_space", [
        "Pouco espaço na pasta temporária ({path}) para extrair o jogo antes de gerar o PKG: livre {free}, necessário {need} (mais uma folga). Libere espaço ou aponte a variável TEMP para outro disco.",
        "Not enough space in the temporary folder ({path}) to extract the game before building the PKG: free {free}, needed {need} (plus some margin). Free some space or point the TEMP variable to another drive.",
        "Poco espacio en la carpeta temporal ({path}) para extraer el juego antes de generar el PKG: libre {free}, necesario {need} (más un margen). Libera espacio o apunta la variable TEMP a otro disco.",
        "Pas assez d'espace dans le dossier temporaire ({path}) pour extraire le jeu avant de générer le PKG : libre {free}, nécessaire {need} (plus une marge). Libérez de l'espace ou faites pointer la variable TEMP vers un autre disque.",
        "Недостаточно места во временной папке ({path}) для извлечения игры перед сборкой PKG: свободно {free}, нужно {need} (плюс запас). Освободите место или направьте переменную TEMP на другой диск.",
    ]),
    ("err.pkg_builder_missing", [
        "Builder de PKG não encontrado. Ele vai embutido no aplicativo; em desenvolvimento, compile tools/ps5pkg-builder com o SDK do .NET 10.",
        "PKG builder not found. It is embedded in the application; in development, build tools/ps5pkg-builder with the .NET 10 SDK.",
        "No se encontró el builder de PKG. Va incluido en la aplicación; en desarrollo, compila tools/ps5pkg-builder con el SDK de .NET 10.",
        "Builder PKG introuvable. Il est intégré à l'application ; en développement, compilez tools/ps5pkg-builder avec le SDK .NET 10.",
        "Сборщик PKG не найден. Он встроен в приложение; при разработке соберите tools/ps5pkg-builder с помощью .NET 10 SDK.",
    ]),
    ("err.pkg_builder_spawn", [
        "Falha ao iniciar o builder de PKG: {e}",
        "Failed to start the PKG builder: {e}",
        "No se pudo iniciar el builder de PKG: {e}",
        "Impossible de lancer le builder PKG : {e}",
        "Не удалось запустить сборщик PKG: {e}",
    ]),
    ("err.pkg_builder_failed", [
        "O builder de PKG falhou.",
        "The PKG builder failed.",
        "El builder de PKG falló.",
        "Le builder PKG a échoué.",
        "Сборщик PKG завершился с ошибкой.",
    ]),
    ("err.pkg_missing_dll", [
        "A conversão de PKG precisa do arquivo ProsperoPkgTool.Data.dll, que não está incluído. Coloque-o na pasta \"dll's\", ao lado do PS5GFC.exe (veja o README).",
        "PKG support needs the ProsperoPkgTool.Data.dll file, which is not included. Put it in the \"dll's\" folder next to PS5GFC.exe (see the README).",
        "La conversión a PKG necesita el archivo ProsperoPkgTool.Data.dll, que no está incluido. Colócalo en la carpeta \"dll's\", junto a PS5GFC.exe (consulta el README).",
        "La conversion en PKG a besoin du fichier ProsperoPkgTool.Data.dll, qui n'est pas inclus. Placez-le dans le dossier « dll's », à côté de PS5GFC.exe (voir le README).",
        "Для конвертации в PKG нужен файл ProsperoPkgTool.Data.dll, который не включён в поставку. Поместите его в папку «dll's» рядом с PS5GFC.exe (см. README).",
    ]),
    ("err.pkg_disk_insufficient", [
        "Espaço insuficiente: é preciso ~{need} GB em {root} ({what}), e há {have} GB livres.",
        "Not enough free space: need ~{need} GB on {root} ({what}), have {have} GB free.",
        "Espacio insuficiente: se necesitan ~{need} GB en {root} ({what}), y hay {have} GB libres.",
        "Espace insuffisant : il faut ~{need} Go sur {root} ({what}), il reste {have} Go libres.",
        "Недостаточно места: нужно ~{need} ГБ на {root} ({what}), свободно {have} ГБ.",
    ]),
    ("err.pkg_disk_what_both", [
        "pasta temporária e saída",
        "temp workspace and output",
        "carpeta temporal y salida",
        "dossier temporaire et sortie",
        "временная папка и результат",
    ]),
    ("err.pkg_disk_what_temp", [
        "pasta temporária",
        "temp workspace",
        "carpeta temporal",
        "dossier temporaire",
        "временная папка",
    ]),
    ("err.pkg_disk_what_output", [
        "pacote de saída",
        "output package",
        "paquete de salida",
        "paquet de sortie",
        "выходной пакет",
    ]),
    ("err.exists", [
        "O arquivo de saída já existe: {path}",
        "The output file already exists: {path}",
        "El archivo de salida ya existe: {path}",
        "Le fichier de sortie existe déjà : {path}",
        "Выходной файл уже существует: {path}",
    ]),
    ("err.dir_not_empty", [
        "A pasta de saída não está vazia: {path}",
        "The output folder is not empty: {path}",
        "La carpeta de salida no está vacía: {path}",
        "Le dossier de sortie n'est pas vide : {path}",
        "Выходная папка не пуста: {path}",
    ]),
    ("err.no_space", [
        "Espaço insuficiente no destino: {free} livres, são necessários pelo menos ~{need}",
        "Not enough space on the destination: {free} free, at least ~{need} needed",
        "Espacio insuficiente en el destino: {free} libres, se necesitan al menos ~{need}",
        "Espace insuffisant sur la destination : {free} libres, au moins ~{need} nécessaires",
        "Недостаточно места на диске назначения: свободно {free}, нужно не менее ~{need}",
    ]),
    ("warn.low_space", [
        "Pouco espaço livre no destino ({free})",
        "Low free space on the destination ({free})",
        "Poco espacio libre en el destino ({free})",
        "Peu d'espace libre sur la destination ({free})",
        "Мало свободного места на диске назначения ({free})",
    ]),
    ("err.cancelled_user", [
        "Cancelado pelo usuário. O arquivo parcial foi removido.",
        "Cancelled by the user. The partial file was removed.",
        "Cancelado por el usuario. Se eliminó el archivo parcial.",
        "Annulé par l'utilisateur. Le fichier partiel a été supprimé.",
        "Отменено пользователем. Частичный файл удалён.",
    ]),
    ("err.access", [
        "Não foi possível acessar {path}: {err}",
        "Cannot access {path}: {err}",
        "No se puede acceder a {path}: {err}",
        "Impossible d'accéder à {path} : {err}",
        "Нет доступа к {path}: {err}",
    ]),
    ("err.archive", [
        "Arquivos compactados (.zip/.rar/.7z) não são suportados",
        "Archives (.zip/.rar/.7z) are not supported",
        "Los archivos comprimidos (.zip/.rar/.7z) no son compatibles",
        "Les archives (.zip/.rar/.7z) ne sont pas prises en charge",
        "Архивы (.zip/.rar/.7z) не поддерживаются",
    ]),
    ("err.bare_pfsc", [
        "Um stream PFSC solto (sem PFS externo) não é uma origem válida",
        "A bare PFSC stream (without an outer PFS) is not a valid source",
        "Un flujo PFSC suelto (sin PFS externo) no es un origen válido",
        "Un flux PFSC isolé (sans PFS externe) n'est pas une source valide",
        "Отдельный поток PFSC (без внешнего PFS) не является допустимым источником",
    ]),
    ("err.unknown_format", [
        "Formato não reconhecido: {path}",
        "Unrecognized format: {path}",
        "Formato no reconocido: {path}",
        "Format non reconnu : {path}",
        "Неизвестный формат: {path}",
    ]),
    ("err.not_dir", [
        "{path} não é uma pasta",
        "{path} is not a folder",
        "{path} no es una carpeta",
        "{path} n'est pas un dossier",
        "{path} — не папка",
    ]),
    ("err.bad_chars", [
        "Nome de arquivo com caracteres inválidos em {path}",
        "File name with invalid characters in {path}",
        "Nombre de archivo con caracteres no válidos en {path}",
        "Nom de fichier avec des caractères invalides dans {path}",
        "Имя файла с недопустимыми символами в {path}",
    ]),
    ("err.is_dir", [
        "{path} é um diretório",
        "{path} is a directory",
        "{path} es un directorio",
        "{path} est un répertoire",
        "{path} — каталог",
    ]),
    ("err.bad_index", [
        "Índice de arquivo inválido",
        "Invalid file index",
        "Índice de archivo no válido",
        "Index de fichier invalide",
        "Недопустимый индекс файла",
    ]),
    ("err.too_big_mem", [
        "{path} é grande demais para ser lido em memória",
        "{path} is too large to read into memory",
        "{path} es demasiado grande para leerlo en memoria",
        "{path} est trop volumineux pour être lu en mémoire",
        "{path} слишком велик для чтения в память",
    ]),
    ("err.unsafe_path", [
        "Caminho inseguro dentro da imagem: {path}",
        "Unsafe path inside the image: {path}",
        "Ruta insegura dentro de la imagen: {path}",
        "Chemin non sûr dans l'image : {path}",
        "Небезопасный путь внутри образа: {path}",
    ]),
    ("err.win_names", [
        "Alguns nomes não são válidos no Windows (ex.: {names}). Converta para uma imagem em vez de uma pasta.",
        "Some names are not valid on Windows (e.g. {names}). Convert to an image instead of a folder.",
        "Algunos nombres no son válidos en Windows (p. ej.: {names}). Convierte a una imagen en lugar de una carpeta.",
        "Certains noms ne sont pas valides sous Windows (ex. : {names}). Convertissez vers une image plutôt qu'un dossier.",
        "Некоторые имена недопустимы в Windows (например: {names}). Конвертируйте в образ, а не в папку.",
    ]),

    ("err.io", ["Erro de E/S: {e}", "I/O error: {e}", "Error de E/S: {e}", "Erreur d'E/S : {e}", "Ошибка ввода-вывода: {e}"]),
    ("err.format", [
        "Imagem inválida: {e}",
        "Invalid image: {e}",
        "Imagen no válida: {e}",
        "Image invalide : {e}",
        "Недопустимый образ: {e}",
    ]),
    ("err.unsupported", [
        "Não suportado: {e}",
        "Not supported: {e}",
        "No compatible: {e}",
        "Non pris en charge : {e}",
        "Не поддерживается: {e}",
    ]),
    ("err.cancelled", [
        "Operação cancelada",
        "Operation cancelled",
        "Operación cancelada",
        "Opération annulée",
        "Операция отменена",
    ]),

    ("verr.count", [
        "A quantidade de arquivos difere: origem {a} × resultado {b}",
        "File count differs: source {a} × result {b}",
        "La cantidad de archivos difiere: origen {a} × resultado {b}",
        "Le nombre de fichiers diffère : source {a} × résultat {b}",
        "Количество файлов различается: источник {a} × результат {b}",
    ]),
    ("verr.missing", [
        "Arquivo ausente no resultado: {path}",
        "File missing from the result: {path}",
        "Archivo ausente en el resultado: {path}",
        "Fichier absent du résultat : {path}",
        "Файл отсутствует в результате: {path}",
    ]),
    ("verr.size", [
        "O tamanho difere em {path}: {a} × {b}",
        "Size differs in {path}: {a} × {b}",
        "El tamaño difiere en {path}: {a} × {b}",
        "La taille diffère pour {path} : {a} × {b}",
        "Размер отличается у {path}: {a} × {b}",
    ]),
    ("verr.content", [
        "O conteúdo difere em {path}",
        "Content differs in {path}",
        "El contenido difiere en {path}",
        "Le contenu diffère pour {path}",
        "Содержимое отличается у {path}",
    ]),
    ("verr.format", [
        "O formato da saída é {got} (esperado {want})",
        "The output format is {got} (expected {want})",
        "El formato de salida es {got} (se esperaba {want})",
        "Le format de sortie est {got} (attendu {want})",
        "Формат результата: {got} (ожидалось {want})",
    ]),

    ("fmt.folder", ["Pasta", "Folder", "Carpeta", "Dossier", "Папка"]),
    ("fmt.inner", ["{fs} interno", "{fs} inside", "{fs} interno", "{fs} interne", "{fs} внутри"]),

    ("iss.no_eboot", [
        "Não há eboot.bin na raiz: o ShadowMountPlus pode não reconhecer o jogo.",
        "No eboot.bin in the root: ShadowMountPlus may not recognize the game.",
        "No hay eboot.bin en la raíz: ShadowMountPlus podría no reconocer el juego.",
        "Pas d'eboot.bin à la racine : ShadowMountPlus pourrait ne pas reconnaître le jeu.",
        "В корне нет eboot.bin: ShadowMountPlus может не распознать игру.",
    ]),
    ("iss.no_titleid", [
        "sce_sys/param.json ausente ou sem titleId.",
        "sce_sys/param.json is missing or has no titleId.",
        "sce_sys/param.json no existe o no tiene titleId.",
        "sce_sys/param.json est absent ou sans titleId.",
        "sce_sys/param.json отсутствует или не содержит titleId.",
    ]),
    ("iss.no_contentid", [
        "sce_sys/param.json ausente ou sem contentId; PKG indisponível.",
        "sce_sys/param.json is missing or has no contentId; PKG is unavailable.",
        "sce_sys/param.json no existe o no tiene contentId; PKG no está disponible.",
        "sce_sys/param.json est absent ou sans contentId ; PKG indisponible.",
        "sce_sys/param.json отсутствует или не содержит contentId; PKG недоступен.",
    ]),
    ("iss.cluster", [
        "Cluster exFAT de {v}: o ShadowMountPlus prefere 64 KiB (ative “Reconstruir” para corrigir).",
        "exFAT cluster of {v}: ShadowMountPlus prefers 64 KiB (enable “Rebuild” to fix).",
        "Clúster exFAT de {v}: ShadowMountPlus prefiere 64 KiB (activa «Reconstruir» para corregirlo).",
        "Cluster exFAT de {v} : ShadowMountPlus préfère 64 KiB (activez « Reconstruire » pour corriger).",
        "Кластер exFAT {v}: ShadowMountPlus предпочитает 64 КиБ (включите «Пересобрать» для исправления).",
    ]),
    ("iss.block", [
        "Bloco de {v}: o padrão do ShadowMountPlus é 64 KiB.",
        "Block size of {v}: the ShadowMountPlus default is 64 KiB.",
        "Bloque de {v}: el valor por defecto de ShadowMountPlus es 64 KiB.",
        "Bloc de {v} : la valeur par défaut de ShadowMountPlus est 64 KiB.",
        "Блок {v}: по умолчанию ShadowMountPlus использует 64 КиБ.",
    ]),
    ("iss.pfs_ascii", [
        "PFS só aceita nomes ASCII (ex.: {names}).",
        "PFS only accepts ASCII names (e.g. {names}).",
        "PFS solo acepta nombres ASCII (p. ej.: {names}).",
        "PFS n'accepte que des noms ASCII (ex. : {names}).",
        "PFS принимает только ASCII-имена (например: {names}).",
    ]),
    ("iss.exfat_long", [
        "Nome longo demais para exFAT (>255): {path}",
        "Name too long for exFAT (>255): {path}",
        "Nombre demasiado largo para exFAT (>255): {path}",
        "Nom trop long pour exFAT (>255) : {path}",
        "Имя слишком длинное для exFAT (>255): {path}",
    ]),
    ("iss.ufs2_long", [
        "Nome longo demais para UFS2 (>255 bytes): {path}",
        "Name too long for UFS2 (>255 bytes): {path}",
        "Nombre demasiado largo para UFS2 (>255 bytes): {path}",
        "Nom trop long pour UFS2 (>255 octets) : {path}",
        "Имя слишком длинное для UFS2 (>255 байт): {path}",
    ]),
    ("iss.same_format", [
        "A origem já está neste formato.",
        "The source is already in this format.",
        "El origen ya está en este formato.",
        "La source est déjà dans ce format.",
        "Источник уже в этом формате.",
    ]),
    ("iss.win_name", [
        "Nome inválido no Windows para extrair em pasta: {path}",
        "Name not valid on Windows for extraction to a folder: {path}",
        "Nombre no válido en Windows para extraer a una carpeta: {path}",
        "Nom non valide sous Windows pour l'extraction vers un dossier : {path}",
        "Имя недопустимо в Windows для извлечения в папку: {path}",
    ]),

    ("err.rm.unreachable", [
        "Não foi possível falar com o console: {e}",
        "Could not reach the console: {e}",
        "No se pudo contactar con la consola: {e}",
        "Impossible de joindre la console : {e}",
        "Не удалось связаться с консолью: {e}",
    ]),
    ("err.rm.busy", [
        "O console está ocupado com outra operação de arquivos. Tente de novo em instantes.",
        "The console is busy with another file operation. Try again in a moment.",
        "La consola está ocupada con otra operación de archivos. Inténtalo de nuevo en unos instantes.",
        "La console est occupée par une autre opération sur les fichiers. Réessayez dans un instant.",
        "Консоль занята другой файловой операцией. Повторите попытку через несколько секунд.",
    ]),
    ("err.rm.exists", [
        "Já existe no console: {e}",
        "Already exists on the console: {e}",
        "Ya existe en la consola: {e}",
        "Existe déjà sur la console : {e}",
        "Уже существует на консоли: {e}",
    ]),
    ("err.rm.notfound", [
        "Não encontrado no console: {e}",
        "Not found on the console: {e}",
        "No se encontró en la consola: {e}",
        "Introuvable sur la console : {e}",
        "Не найдено на консоли: {e}",
    ]),
    ("err.rm.nospace", [
        "Sem espaço no console: {e}",
        "Not enough space on the console: {e}",
        "Sin espacio en la consola: {e}",
        "Espace insuffisant sur la console : {e}",
        "Недостаточно места на консоли: {e}",
    ]),
    ("err.rm.denied", [
        "O console recusou: {e}",
        "The console refused: {e}",
        "La consola lo rechazó: {e}",
        "La console a refusé : {e}",
        "Консоль отказала: {e}",
    ]),
    ("err.rm.protocol", [
        "Esse endereço não respondeu como o servidor esperado (Prospero Manager ou FTP): {e}",
        "That address did not answer like the expected server (Prospero Manager or FTP): {e}",
        "Esa dirección no respondió como el servidor esperado (Prospero Manager o FTP): {e}",
        "Cette adresse n'a pas répondu comme le serveur attendu (Prospero Manager ou FTP) : {e}",
        "Этот адрес отвечает не как ожидаемый сервер (Prospero Manager или FTP): {e}",
    ]),
    ("err.rm.not_connected", [
        "Nenhum console conectado.",
        "No console connected.",
        "Ninguna consola conectada.",
        "Aucune console connectée.",
        "Консоль не подключена.",
    ]),
    ("err.rm.other", ["Console: {e}", "Console: {e}", "Consola: {e}", "Console : {e}", "Консоль: {e}"]),
    ("err.rm.bad_addr", [
        "Endereço do console inválido: {addr}",
        "Invalid console address: {addr}",
        "Dirección de la consola no válida: {addr}",
        "Adresse de la console invalide : {addr}",
        "Недопустимый адрес консоли: {addr}",
    ]),
    ("log.rm.target", [
        "Enviando direto ao console {host}: {path}",
        "Sending straight to the console {host}: {path}",
        "Enviando directo a la consola {host}: {path}",
        "Envoi direct vers la console {host} : {path}",
        "Отправка прямо на консоль {host}: {path}",
    ]),
    ("log.rm.folder", [
        "Criando as pastas no console e enviando os arquivos ({conns} conexões)…",
        "Creating the folders on the console and sending the files ({conns} connections)…",
        "Creando las carpetas en la consola y enviando los archivos ({conns} conexiones)…",
        "Création des dossiers sur la console et envoi des fichiers ({conns} connexions)…",
        "Создание папок на консоли и отправка файлов (соединений: {conns})…",
    ]),
    ("log.rm.send_image", [
        "Gerando a imagem e enviando ao console em tempo real…",
        "Building the image and sending it to the console as it goes…",
        "Generando la imagen y enviándola a la consola en tiempo real…",
        "Génération de l'image et envoi vers la console en temps réel…",
        "Создание образа и отправка на консоль в реальном времени…",
    ]),
    ("log.rm.measure", [
        "Passo 1 de 2: comprimindo só para medir o tamanho final (nada é enviado ainda)…",
        "Step 1 of 2: compressing only to measure the final size (nothing is sent yet)…",
        "Paso 1 de 2: comprimiendo solo para medir el tamaño final (aún no se envía nada)…",
        "Étape 1 sur 2 : compression pour mesurer la taille finale (rien n'est encore envoyé)…",
        "Шаг 1 из 2: сжатие только для измерения итогового размера (пока ничего не отправляется)…",
    ]),
    ("log.rm.send", [
        "Passo 2 de 2: comprimindo de novo e enviando ao console ({size})…",
        "Step 2 of 2: compressing again and sending to the console ({size})…",
        "Paso 2 de 2: comprimiendo de nuevo y enviando a la consola ({size})…",
        "Étape 2 sur 2 : nouvelle compression et envoi vers la console ({size})…",
        "Шаг 2 из 2: повторное сжатие и отправка на консоль ({size})…",
    ]),
    ("log.rm.verify", [
        "Conferindo o que chegou ao console: baixando de volta e comparando o CRC32…",
        "Checking what reached the console: downloading it back and comparing the CRC32…",
        "Comprobando lo que llegó a la consola: descargándolo de vuelta y comparando el CRC32…",
        "Vérification de ce qui est arrivé sur la console : retéléchargement et comparaison du CRC32…",
        "Проверка данных на консоли: загрузка обратно и сравнение CRC32…",
    ]),
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_entry_is_complete_and_placeholders_match() {
        fn ph(s: &str) -> Vec<String> {
            let mut v: Vec<String> = Vec::new();
            let mut rest = s;
            while let Some(i) = rest.find('{') {
                match rest[i..].find('}') {
                    Some(j) => {
                        v.push(rest[i..i + j + 1].to_string());
                        rest = &rest[i + j + 1..];
                    }
                    None => break,
                }
            }
            v.sort();
            v
        }
        for (k, v) in TABLE {
            for (i, s) in v.iter().enumerate() {
                assert!(!s.is_empty(), "{k}[{i}] vazio");
            }
            for i in 1..5 {
                assert_eq!(ph(v[0]), ph(v[i]), "placeholders de {k} divergem no idioma {i}");
            }
        }
    }

    #[test]
    fn keys_are_unique() {
        let mut ks: Vec<&str> = TABLE.iter().map(|(k, _)| *k).collect();
        ks.sort();
        let n = ks.len();
        ks.dedup();
        assert_eq!(n, ks.len());
    }

    #[test]
    fn macro_replaces_and_falls_back() {
        set_lang("ru");
        assert!(t!("err.cancelled").contains("отмен"));
        set_lang("pt-BR");
        assert_eq!(t!("err.exists", path = "x.bin"), "O arquivo de saída já existe: x.bin");
        set_lang("en");
        assert_eq!(tr("chave.inexistente"), "chave.inexistente");
    }
}
