namespace CoreAI.Mods.Folders
{
    /// <summary>
    /// Every way a mod folder path, a header edit or a folder operation can fail. Each value has a stable
    /// upper-case code (<see cref="ModFolderErrors.Code"/>) that tools and logs report verbatim.
    /// </summary>
    public enum ModFolderErrorCode
    {
        /// <summary>No error.</summary>
        None = 0,

        /// <summary>A path has an empty segment between two separators (<c>Games//Arcade</c>).</summary>
        EmptySegment,

        /// <summary>A segment is longer than <see cref="ModFolderPath.MaxSegmentLength"/> characters.</summary>
        SegmentTooLong,

        /// <summary>A path is longer than <see cref="ModFolderPath.MaxLength"/> characters.</summary>
        PathTooLong,

        /// <summary>A path has more than <see cref="ModFolderPath.MaxDepth"/> segments.</summary>
        TooDeep,

        /// <summary>A segment holds a character outside the allowed set (<see cref="ModFolderPath.AllowedPunctuation"/>).</summary>
        InvalidCharacter,

        /// <summary>A segment is <c>.</c> or <c>..</c>, which read as navigation rather than as a name.</summary>
        ReservedName,

        /// <summary>A folder name given for a rename holds a separator; a name is one segment.</summary>
        InvalidName,

        /// <summary>The operation needs a folder and was given the top level (rename, move or delete of the root).</summary>
        RootNotAllowed,

        /// <summary>No folder exists at the given path.</summary>
        FolderNotFound,

        /// <summary>No mod exists with the given id.</summary>
        ModNotFound,

        /// <summary>A folder already exists at the target path (names compare case-insensitively).</summary>
        FolderAlreadyExists,

        /// <summary>A folder to delete still holds mods somewhere in its branch.</summary>
        FolderNotEmpty,

        /// <summary>A folder was moved into itself or into one of its own subfolders.</summary>
        MoveIntoItself,

        /// <summary>The mod source starts a <c>--[[@coreai</c> header that never closes, so it cannot be edited safely.</summary>
        HeaderUnclosed,

        /// <summary>The store refused or failed a write; the message names the mod or folder and the reason.</summary>
        StoreFailed,

        /// <summary>A persisted folder document names a format or version this reader does not know.</summary>
        UnsupportedFormat
    }

    /// <summary>Stable codes and default wording for <see cref="ModFolderErrorCode"/>.</summary>
    public static class ModFolderErrors
    {
        /// <summary>The stable upper-case code of <paramref name="error"/>, for example <c>FOLDER_NOT_EMPTY</c>.</summary>
        public static string Code(ModFolderErrorCode error)
        {
            switch (error)
            {
                case ModFolderErrorCode.None: return "OK";
                case ModFolderErrorCode.EmptySegment: return "EMPTY_SEGMENT";
                case ModFolderErrorCode.SegmentTooLong: return "SEGMENT_TOO_LONG";
                case ModFolderErrorCode.PathTooLong: return "PATH_TOO_LONG";
                case ModFolderErrorCode.TooDeep: return "TOO_DEEP";
                case ModFolderErrorCode.InvalidCharacter: return "INVALID_CHARACTER";
                case ModFolderErrorCode.ReservedName: return "RESERVED_NAME";
                case ModFolderErrorCode.InvalidName: return "INVALID_NAME";
                case ModFolderErrorCode.RootNotAllowed: return "ROOT_NOT_ALLOWED";
                case ModFolderErrorCode.FolderNotFound: return "FOLDER_NOT_FOUND";
                case ModFolderErrorCode.ModNotFound: return "MOD_NOT_FOUND";
                case ModFolderErrorCode.FolderAlreadyExists: return "FOLDER_ALREADY_EXISTS";
                case ModFolderErrorCode.FolderNotEmpty: return "FOLDER_NOT_EMPTY";
                case ModFolderErrorCode.MoveIntoItself: return "MOVE_INTO_ITSELF";
                case ModFolderErrorCode.HeaderUnclosed: return "HEADER_UNCLOSED";
                case ModFolderErrorCode.StoreFailed: return "STORE_FAILED";
                case ModFolderErrorCode.UnsupportedFormat: return "UNSUPPORTED_FORMAT";
                default: return "UNKNOWN";
            }
        }
    }
}
