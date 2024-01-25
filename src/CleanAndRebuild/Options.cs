// Copyright (c) Chris Pulman. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace CPCleanAndRebuild
{
    /// <summary>
    /// Options.
    /// </summary>
    /// <seealso cref="DialogPage" />
    public class Options : DialogPage
    {
        /// <summary>
        /// Gets or sets the target subdirectories.
        /// </summary>
        /// <value>
        /// The target subdirectories.
        /// </value>
        [Category("General")]
        [DisplayName("Subdirectories to clean")]
        [Description("Subdirectories in project which will be cleaned")]
        [DefaultValue(true)]
        public string[] TargetSubdirectories { get; set; } = ["bin", "obj"];
    }
}
