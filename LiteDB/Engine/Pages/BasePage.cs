﻿using System;
using System.Collections.Generic;
using System.IO;

namespace LiteDB
{
    public enum PageType { Empty = 0, Header = 1, CollectionList = 6, Collection = 2, Index = 3, Data = 4, Extend = 5 }

    internal abstract class BasePage
    {
        /// <summary>
        /// The size of each page in disk
        /// </summary>
        public const int PAGE_SIZE = 8192;

        /// <summary>
        /// This size is used bytes in header pages 33 bytes (+31 reserved to future use) = 64 bytes
        /// </summary>
        public const int PAGE_HEADER_SIZE = 64;

        /// <summary>
        /// Bytes available to store data removing page header size - 8128 bytes
        /// </summary>
        public const int PAGE_AVAILABLE_BYTES = PAGE_SIZE - PAGE_HEADER_SIZE;

        /// <summary>
        /// Represent page number - start in 0 with HeaderPage [4 bytes]
        /// </summary>
        public uint PageID { get; set; }

        /// <summary>
        /// Indicate the page type [1 byte] - Must be implemented for each page type
        /// </summary>
        public abstract PageType PageType { get; }

        /// <summary>
        /// Represent the previous page. Used for page-sequences - MaxValue represent that has NO previous page [4 bytes]
        /// </summary>
        public uint PrevPageID { get; set; }

        /// <summary>
        /// Represent the next page. Used for page-sequences - MaxValue represent that has NO next page [4 bytes]
        /// </summary>
        public uint NextPageID { get; set; }

        /// <summary>
        /// Used for all pages to count items inside this page(bytes, nodes, blocks, ...) [2 bytes]
        /// Its Int32 but writes in UInt16
        /// </summary>
        public int ItemCount { get; set; }

        /// <summary>
        /// Used to find a free page using only header search [used in FreeList] [2 bytes]
        /// Its Int32 but writes in UInt16
        /// Its updated when a page modify content length (add/remove items)
        /// </summary>
        public int FreeBytes { get; set; }

        /// <summary>
        /// Represent transaction page ID that was stored [16 bytes]
        /// </summary>
        public Guid TransactionID { get; set; }

        /// <summary>
        /// Set this pages that was changed and must be persist in disk [not peristable]
        /// </summary>
        public bool IsDirty { get; set; }

        public BasePage()
        {
        }

        public BasePage(uint pageID)
        {
            this.PageID = pageID;
            this.PrevPageID = uint.MaxValue;
            this.NextPageID = uint.MaxValue;
            this.ItemCount = 0;
            this.FreeBytes = PAGE_AVAILABLE_BYTES;
            this.TransactionID = Guid.Empty;
            this.IsDirty = false;
        }

        #region Read/Write page

        /// <summary>
        /// Write a page to byte array
        /// </summary>
        public void WritePage(BinaryWriter writer)
        {
            var start = writer.BaseStream.Position;

            // if need write on initial file (position = 0) must skip first header area
            // becase are locked and contains no valid page data (password/salt info)
            if (start == 0)
            {
                writer.Seek(PAGE_HEADER_SIZE, SeekOrigin.Current);
            }
            else
            {
                this.WriteHeader(writer);
            }

            this.WriteContent(writer);

            // padding end of page with 0 byte
            var length = BasePage.PAGE_SIZE - (writer.BaseStream.Position - start);

            if (length > 0)
            {
                writer.Write(new byte[length]);
            }
        }

        private void ReadHeader(BinaryReader reader)
        {
            // first 5 bytes (pageID + pageType) was read before class create by [static ReadPage(long position)]
            // this.PageID // 4 bytes
            // this.PageType // 1 byte

            this.PrevPageID = reader.ReadUInt32(); // 4 bytes
            this.NextPageID = reader.ReadUInt32(); // 4 bytes
            this.ItemCount = reader.ReadUInt16(); // 2 bytes
            this.FreeBytes = reader.ReadUInt16(); // 2 bytes
            this.TransactionID = reader.ReadGuid(); // 16 bytes

            reader.ReadBytes(31); // reserved 31 bytes
        }

        private void WriteHeader(BinaryWriter writer)
        {
            writer.Write(this.PageID);
            writer.Write((byte)this.PageType);

            writer.Write(this.PrevPageID);
            writer.Write(this.NextPageID);
            writer.Write((UInt16)this.ItemCount);
            writer.Write((UInt16)this.FreeBytes);
            writer.Write(this.TransactionID);

            writer.Write(new byte[31]);
        }

        protected abstract void ReadContent(BinaryReader reader, bool utcDate);

        protected abstract void WriteContent(BinaryWriter writer);

        #endregion

        #region Static Helper Methods

        /// <summary>
        /// Returns a size of specified number of pages
        /// </summary>
        public static long GetPagePosition(uint pageID)
        {
            return checked((long)pageID * BasePage.PAGE_SIZE);
        }

        /// <summary>
        /// Returns a size of specified number of pages
        /// </summary>
        public static long GetPagePosition(int pageID)
        {
            if (pageID < 0) throw new ArgumentOutOfRangeException(nameof(pageID), "Could not be less than 0.");

            return BasePage.GetPagePosition((uint)pageID);
        }

        /// <summary>
        /// Create a new instance of page based on T type
        /// </summary>
        public static T CreateInstance<T>(uint pageID)
            where T : BasePage
        {
            var type = typeof(T);

            // casting using "as T" #90 / thanks @Skysper
            if (type == typeof(HeaderPage)) return new HeaderPage(pageID) as T;
            if (type == typeof(CollectionPage)) return new CollectionPage(pageID) as T;
            if (type == typeof(IndexPage)) return new IndexPage(pageID) as T;
            if (type == typeof(DataPage)) return new DataPage(pageID) as T;
            if (type == typeof(ExtendPage)) return new ExtendPage(pageID) as T;
            if (type == typeof(EmptyPage)) return new EmptyPage(pageID) as T;

            throw new Exception("Invalid base page type T");
        }

        /// <summary>
        /// Create a new instance of page based on PageType
        /// </summary>
        public static BasePage CreateInstance(uint pageID, PageType pageType)
        {
            switch (pageType)
            {
                case PageType.Collection: return new CollectionPage(pageID);
                case PageType.Index: return new IndexPage(pageID);
                case PageType.Data: return new DataPage(pageID);
                case PageType.Extend: return new ExtendPage(pageID);
                case PageType.Empty: return new EmptyPage(pageID);
                // use Header as default, because header page will read fixed HEADER_INFO and validate file format (if is not valid datafile)
                default: return new HeaderPage(pageID);
            }
        }

        /// <summary>
        /// Read a page with correct instance page object. Checks for pageType
        /// </summary>
        public static BasePage ReadPage(BinaryReader reader, bool utcDate)
        {
            var start = reader.BaseStream.Position;
            BasePage page;
            
            // if are reading from position 0 (initial file) skip header area from header page (first 64 bytes)
            // this area are locked and have non-valid data (contains hash-password and salt - non encrypted data)
            if (start == 0)
            {
                reader.BaseStream.Seek(PAGE_HEADER_SIZE, SeekOrigin.Current);

                page = new HeaderPage(0);
            }
            else
            {
                var pageID = reader.ReadUInt32();
                var pageType = (PageType)reader.ReadByte();

                page = BasePage.CreateInstance(pageID, pageType);

                page.ReadHeader(reader);
            }

            // read content
            page.ReadContent(reader, utcDate);

            var length = BasePage.PAGE_SIZE - (reader.BaseStream.Position - start);

            if (length > 0)
            {
                reader.ReadBytes((int)length);
            }

            return page;
        }

        #endregion

        /// <summary>
        /// Make clone instance of this Page - by default: convert to bytes and read again (can be optimized)
        /// </summary>
        public abstract BasePage Clone();

        public override string ToString()
        {
            return this.PageID.ToString().PadLeft(4, '0') + " : " + this.PageType + " (" + this.ItemCount + ")";
        }
    }
}