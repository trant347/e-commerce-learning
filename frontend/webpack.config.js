const path = require('path');
const MiniCssExtractPlugin = require('mini-css-extract-plugin');
const HtmlWebpackPlugin = require('html-webpack-plugin'); // For HTML generation
const { CleanWebpackPlugin } = require('clean-webpack-plugin');
const createStyledComponentsTransformer = require('typescript-plugin-styled-components').default;
// 2. create a transformer;
// the factory additionally accepts an options object which described below
const styledComponentsTransformer = createStyledComponentsTransformer();

module.exports = {
  mode: 'development', // or 'production' for optimized build
  entry: './index.tsx', // Your main entry point (usually src/index.js)
  output: {
    path: path.resolve(__dirname, "../public"),
    filename: "[name].bundle.js",
    chunkFilename: '[name].js',
    publicPath: '/', // Important for React Router and serving from different paths
  },
  devtool: 'source-map', // Add source maps for debugging
  module: {
    rules: [
     {
        test: /\.tsx?$/,
        loader: 'ts-loader',
        options: {
            getCustomTransformers: () => ({ before: [styledComponentsTransformer] })
        }
      },
      {
        test: /\.css$/i, // Handle CSS files
        use: [
          MiniCssExtractPlugin.loader,
          {
            loader: 'css-loader',
            options: {
              importLoaders: 1, // Process imported CSS (e.g., in @import statements)
            },
          },
        ],
      },
      {
        test: /\.(png|svg|jpg|jpeg|gif)$/i, // Handle images
        type: 'asset/resource',
      },
      {
        test: /\.(woff|woff2|eot|ttf|otf)$/i, // Font files
        use: [
          {
            loader: 'url-loader',
            options: {
              limit: 8192, // Inline files smaller than 8kb (adjust as needed)
              name: '[name].[ext]', // Output filename (no hash for fonts usually)
              outputPath: 'fonts', // Output directory for fonts
              publicPath: 'fonts', // Important for correct paths in CSS
            },
          },
        ],
     }
    ],
  },
  resolve: {
    extensions: [".ts", ".tsx", ".js"],
    alias: { // Useful for shorter import paths
      '@components': path.resolve(__dirname, './components'),
    },
  },
  plugins: [
    new CleanWebpackPlugin(),
    new MiniCssExtractPlugin({
      filename: '[name].[contenthash].css', // Use hashes for caching,
      chunkFilename: "[id].css"
    }),
    new HtmlWebpackPlugin({
      template: './index.html', // Path to your HTML template
      filename: 'index.html', // Output HTML file name
    }),
  ],
  optimization: {
    splitChunks: {
        cacheGroups: {
            default:false,
            vendor: {
                test: /node_modules/,
                chunks: "initial",
                name: "vendor",
                priority: 10,
                enforce: true
            },
            common: {
                name: 'common',
                minChunks: 2,
                chunks: 'all',
                priority: 10,
                reuseExistingChunk: true,
                enforce: true
            }
        }
    },
    runtimeChunk: true
}
};